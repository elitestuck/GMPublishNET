﻿using GMPublish.LZMA;
using Ionic.Zip;
using GMPublish.GMAD;
using SteamKit2;
using SteamKit2.Unified.Internal;
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GMPublish
{
    class Program
    {
        static SteamClient steamClient;
        static CallbackManager manager;

        static SteamUser steamUser;

        static SteamUnifiedMessages steamUnifiedMessages;

        static bool isRunning;

        static string user, pass;
        static string authCode, twoFactorAuth;

        public static readonly uint APPID = 4000;

        public static void Main(string[] args)
        {
            //DebugLog.AddListener((level, msg) =>
            //{
            //    Console.WriteLine("[{0}] {1}", level, msg);
            //});

            if (args.Length < 2)
            {
                Console.WriteLine("GMPublish: No username and password specified!");
                return;
            }

            user = args[0];
            pass = args[1];

            SteamDirectory.Initialize().Wait();

            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamUser>();
            steamUnifiedMessages = steamClient.GetHandler<SteamUnifiedMessages>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

            Console.WriteLine("Connecting to Steam...");
            isRunning = true;
            steamClient.Connect();
            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
            Console.WriteLine("Done?");
            Console.ReadLine();
        }

        static byte[] SHAHash(Stream stream)
        {

            using (var sha = new SHA1Managed())
            {
                byte[] hash;
                hash = sha.ComputeHash(stream);
                stream.Seek(0, SeekOrigin.Begin);
                return hash;
            }
        }

        static void FullyLoggedIn(SteamUser.LoggedOnCallback callback)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    await CloudStream.DeleteFile("gmpublish_icon.jpg", APPID, steamClient);
                    await CloudStream.DeleteFile("gmpublish.gma", APPID, steamClient);

                    var baseFolder = "./Addon";
                    var gmaPath = "./addon.gma"; //Path.GetTempFileName();

                    var addonJsonPath = Path.Combine(baseFolder, "addon.json");
                    if (!File.Exists(addonJsonPath)) { throw new Exception("No addon.json file found in specified folder"); }
                    AddonJSON addon;
                    using (FileStream addonStream = new FileStream(addonJsonPath, FileMode.Open))
                    {
                        addon = addonStream.CreateFromJsonStream<AddonJSON>();
                    }
                    if (addon.Icon == "") { throw new Exception("No icon file specified"); }
                    using (Stream gmaStream = new FileStream(gmaPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {

                        GMADCreator.Create(baseFolder, addon, gmaStream);
                        gmaStream.Seek(0, SeekOrigin.Begin);


                        using (var icon = new FileStream(Path.Combine(baseFolder, addon.Icon), FileMode.Open))
                        {
                            var hash = SHAHash(icon);
                            var iconSuccess = await CloudStream.UploadStream("gmpublish_icon.jpg", APPID, hash, icon.Length, steamClient, icon);
                            if (!iconSuccess) { Console.WriteLine("JPG Upload failed"); return; }
                        }

                        var lzmaStream = LZMAEncodeStream.CompressStreamLZMA(gmaStream);
                        var hashGma = SHAHash(lzmaStream);
                        var gmaSuccess = await CloudStream.UploadStream("gmpublish.gma", APPID, hashGma, lzmaStream.Length, steamClient, lzmaStream);
                        if (!gmaSuccess) { Console.WriteLine("GMA Upload failed"); return; }
                    }

                    var publishService = steamUnifiedMessages.CreateService<IPublishedFile>();
                    if (addon.WorkshopID == 0)
                    {
                        var request = new CPublishedFile_Publish_Request
                        {
                            appid = APPID,
                            consumer_appid = APPID,
                            cloudfilename = "gmpublish.gma",
                            preview_cloudfilename = "gmpublish_icon.jpg",
                            title = addon.Title,
                            file_description = addon.Description,
                            file_type = (uint)EWorkshopFileType.Community,
                            visibility = (uint)EPublishedFileVisibility.Public,
                            collection_type = addon.Type,
                        };
                        foreach (var tag in addon.Tags) { request.tags.Add(tag); }

                        var publishCallback = await publishService.SendMessage(publish => publish.Publish(request));
                        var publishResponse = publishCallback.GetDeserializedResponse<CPublishedFile_Publish_Response>();
                        addon.WorkshopID = publishResponse.publishedfileid;

                        using (FileStream addonStream = new FileStream(addonJsonPath, FileMode.Create))
                        {
                            addonStream.WriteObjectToStreamJson(addon);
                        }
                        Console.WriteLine("NEW PUBLISHED ID: " + publishResponse.publishedfileid + ". Wrote to addon.json");
                    }
                    else
                    {
                        var request = new CPublishedFile_Update_Request
                        {
                            image_height = 512,
                            image_width = 512,
                            publishedfileid = addon.WorkshopID,
                            appid = APPID,
                            filename = "gmpublish.gma",
                            preview_filename = "gmpublish_icon.jpg",
                            title = addon.Title,
                            file_description = addon.Description,
                            visibility = (uint)EPublishedFileVisibility.Public,
                        };
                        foreach (var tag in addon.Tags) { request.tags.Add(tag); }

                        var updateCallback = await publishService.SendMessage(publish => publish.Update(request));
                        var updateResponse = updateCallback.GetDeserializedResponse<CPublishedFile_Update_Response>();
                        
                        Console.WriteLine("Addon has been updated");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Console.ReadKey();
                }
            });
            task.Wait();
            steamUser.LogOff();
            isRunning = false;
        }

        #region SteamLogin
        static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to connect to Steam: {0}", callback.Result);

                isRunning = false;
                return;
            }

            Console.WriteLine("Connected to Steam! Logging in '{0}'...", user);

            byte[] sentryHash = null;
            if (File.Exists("sentry.bin"))
            {
                // if we have a saved sentry file, read and sha-1 hash it
                byte[] sentryFile = File.ReadAllBytes("sentry.bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = user,
                Password = pass,

                // in this sample, we pass in an additional authcode
                // this value will be null (which is the default) for our first logon attempt
                AuthCode = authCode,

                // if the account is using 2-factor auth, we'll provide the two factor code instead
                // this will also be null on our first logon attempt
                TwoFactorCode = twoFactorAuth,

                // our subsequent logons use the hash of the sentry file as proof of ownership of the file
                // this will also be null for our first (no authcode) and second (authcode only) logon attempts
                SentryFileHash = sentryHash,
            });
        }
        static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            // after recieving an AccountLogonDenied, we'll be disconnected from steam
            // so after we read an authcode from the user, we need to reconnect to begin the logon flow again

            Console.WriteLine("Disconnected from Steam, reconnecting in 5...");

            Thread.Sleep(TimeSpan.FromSeconds(5));

            steamClient.Connect();
        }

        static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            bool is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            if (isSteamGuard || is2FA)
            {
                Console.WriteLine("This account is SteamGuard protected!");

                if (is2FA)
                {
                    Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                    twoFactorAuth = Console.ReadLine();
                }
                else
                {
                    Console.Write("Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);
                    authCode = Console.ReadLine();
                }

                return;
            }

            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;
                return;
            }

            Console.WriteLine("Successfully logged on!");
            FullyLoggedIn(callback);
        }

        static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        static void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Console.WriteLine("Updating sentryfile...");

            // write out our sentry file
            // ideally we'd want to write to the filename specified in the callback
            // but then this sample would require more code to find the correct sentry file to read during logon
            // for the sake of simplicity, we'll just use "sentry.bin"

            int fileSize;
            byte[] sentryHash;
            using (var fs = File.Open("sentry.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.Seek(callback.Offset, SeekOrigin.Begin);
                fs.Write(callback.Data, 0, callback.BytesToWrite);
                fileSize = (int)fs.Length;

                fs.Seek(0, SeekOrigin.Begin);
                using (var sha = new SHA1CryptoServiceProvider())
                {
                    sentryHash = sha.ComputeHash(fs);
                }
            }

            // inform the steam servers that we're accepting this sentry file
            steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = fileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash,
            });

            Console.WriteLine("Done!");
        }
        #endregion
    }
}
