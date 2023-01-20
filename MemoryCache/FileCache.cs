﻿using ABI_RC.Core.IO;
using ABI_RC.Core.Savior;
using ABI_RC.Systems.Safety.BundleVerifier;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace Zettai
{
    internal class FileCache
    {
        internal static readonly Dictionary<DownloadData.Status, string> StatusText = new Dictionary<DownloadData.Status, string>();
        internal static readonly Dictionary<int, string> PercentageText = new Dictionary<int, string>();

        private static volatile int downloadCountMax = 5;
        private static readonly SemaphoreSlim downloadCounter = new SemaphoreSlim(0, 32);
        private static readonly Dictionary<DownloadTask.ObjectType, (string path, string extention, DirectoryInfo directoryInfo)> names = new Dictionary<DownloadTask.ObjectType, (string path, string extention, DirectoryInfo directoryInfo)>(3);
        private static readonly System.Collections.Concurrent.ConcurrentQueue<DownloadData> DiskWriteQueue = new System.Collections.Concurrent.ConcurrentQueue<DownloadData>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<DownloadData> DiskReadQueue = new System.Collections.Concurrent.ConcurrentQueue<DownloadData>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<DownloadData> DownloadQueue = new System.Collections.Concurrent.ConcurrentQueue<DownloadData>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<DownloadData> HashQueue = new System.Collections.Concurrent.ConcurrentQueue<DownloadData>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<DownloadData> DecryptQueue = new System.Collections.Concurrent.ConcurrentQueue<DownloadData>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<DownloadData> VerifyQueue = new System.Collections.Concurrent.ConcurrentQueue<DownloadData>();
        private static readonly HashSet<FileInfo> FilesToDelete = new HashSet<FileInfo>();
        private static readonly List<Thread> threads = new List<Thread>();
        private static volatile bool AbortThreads = false;
        const int sleepTime = 5;

        internal static void SetMaxDownloadCount(int value) => downloadCountMax = value;
        internal static bool InitDone { get; private set; } = false;
        internal static void AbortAllThreads() => AbortThreads = true;
        internal static void Verify(DownloadData dlData) => VerifyQueue.Enqueue(dlData);
        internal static void WriteToDisk(DownloadData dlData) => DiskWriteQueue.Enqueue(dlData);
        internal static void Decrypt(DownloadData dlData) => DecryptQueue.Enqueue(dlData);
        internal static void Hash(DownloadData dlData) => HashQueue.Enqueue(dlData);
        internal static void ReadFile(DownloadData dlData) => DiskReadQueue.Enqueue(dlData);
        internal static void Download(DownloadData dlData) => DownloadQueue.Enqueue(dlData);
        internal static void DeleteOldFiles()
        {
            foreach (var file in FilesToDelete)
            {
                file.Delete();
            }
            FilesToDelete.Clear();
        }
        internal static FileCheck FileExists(DownloadTask.ObjectType type, string assetId, string fileId)
        {
            var (path, extention, directory) = names[type];
            if (File.Exists($"{path}\\{assetId}-{fileId}.{extention}"))
                return FileCheck.ExistsSameVersion;
            var fileInfos = directory.GetFiles($"{assetId}-*.{extention}", SearchOption.TopDirectoryOnly);
            if (fileInfos.Length == 0)
                return FileCheck.NotExists;
            FilesToDelete.UnionWith(fileInfos);
            return FileCheck.ExistsDifferentVersion;
        }
        internal static string GetFileCachePath(DownloadTask.ObjectType type, string assetId, string fileId)
        {
            var (path, extention, _) = names[type];
            return $"{path}\\{assetId}-{fileId}.{extention}";
        }
        private static void DecryptThread()
        {
            var decrypt = new FastCVRDecrypt();
            while (!AbortThreads)
            {
                Thread.Sleep(sleepTime);
                if (!DecryptQueue.TryDequeue(out var result))
                    continue;

                try
                {
                    result.status = DownloadData.Status.Decrypting;
                    var size = result.fileSize;
                    var sw = new System.Diagnostics.Stopwatch();
                    sw.Start();

                    var key = result.rawKey ?? DownloadManagerHelperFunctions.ConvertToFileKey(result.fileKey);
                    result.decryptedData = decrypt.Decrypt(result.assetId, result.rawData, key);
                    result.DecryptDone = true;

                    sw.Stop();

                    var last = sw.Elapsed;
                    if (MemoryCache.enableLog.Value)
                        MelonLoader.MelonLogger.Msg($"Decrypt: {last.TotalMilliseconds} ms, {size / 1024f / 1024f} MB ");
                    /*
                    // test against the original

                    sw.Restart();
                    var _ = new ABI_RC.Core.CVRDecrypt().Decrypt(result.assetId, result.rawData, key);
                    last = sw.Elapsed;
                    if (MemoryCache.enableLog.Value)
                        MelonLoader.MelonLogger.Msg($"Decrypt (CVR): {last.TotalMilliseconds} ms, {size/ 1024f / 1024f} MB ");
                    
                     */
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Error($"Decrypt failed! '{e.Message}' ");
                    result.DecryptFailed = true;
                }
            }
        }
        private static void WriteToDiskThread()
        {
            while (!AbortThreads)
            {
                Thread.Sleep(sleepTime);
                if (!DiskWriteQueue.TryDequeue(out var result))
                    continue;

                try
                {
                    File.WriteAllBytes(result.filePath, result.rawData);
                    result.FileWriteDone = true;
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Error($"FileWrite failed! '{e.Message}' ");
                    result.FileWriteFailed = true;
                }
            }
        }

        const int bufferSize = 16384;
        const double bufferSize1000Double = bufferSize * 1000d;

        private static async void DownloadAsync(DownloadData data, HttpClient client) 
        {
            HttpResponseMessage httpResponseMessage = client.GetAsync(data.assetUrl, HttpCompletionOption.ResponseHeadersRead).Result;
            if (!httpResponseMessage.IsSuccessStatusCode)
            {
                MelonLoader.MelonLogger.Error($"Download failed! '{httpResponseMessage.StatusCode}' ");
                data.FileWriteFailed = true;
                return;
            }
            using (var webStream = await httpResponseMessage.Content.ReadAsStreamAsync())
            {
                var length = webStream.Length;
                byte[] bytes = new byte[length];
                using (var memoryStream = new MemoryStream(bytes, true))
                {
                    var totalRead = 0;
                    var buffer = new byte[bufferSize];
                    var isMoreToRead = true;
                    var rateLimit = data.rateLimit; //bytes per sec
                    var timeLimit = rateLimit > 0 ? bufferSize1000Double / rateLimit : 1d; // millisec per read event
                    var stopWatch = new System.Diagnostics.Stopwatch();
                    await downloadCounter.WaitAsync(data.cancellationToken);
                    do
                    {
                        stopWatch.Start();
                        var freeSlots = (downloadCounter.CurrentCount + 1f) / downloadCountMax;
                        var actualTimeLimit = timeLimit * freeSlots;
                        var bytesRead = await webStream.ReadAsync(buffer, 0, buffer.Length, data.cancellationToken);
                        if (bytesRead == 0)
                        {
                            isMoreToRead = false;
                            data.PercentageComplete = 100;
                            data.rawData = bytes;
                            data.DownloadDone = true;
                            break;
                        }
                        await memoryStream.WriteAsync(buffer, 0, bytesRead, data.cancellationToken);
                        totalRead += bytesRead;
                        data.PercentageComplete = (int)(totalRead * 100L / length);

                        if (rateLimit == 0)
                            continue;

                        var time = stopWatch.Elapsed.Ticks / 10000d;

                        stopWatch.Reset();
                        if (time == 0d || time >= actualTimeLimit)
                            continue;

                        var sleepTime = actualTimeLimit - time;
                        try
                        {
                            var timespan = new TimeSpan((long)(sleepTime * 10000));
                            await System.Threading.Tasks.Task.Delay(timespan);
                        }
                        catch (ThreadAbortException) { }
                    }
                    while (isMoreToRead);
                }
            }
            downloadCounter.Release();
        }
        
        private static void DownloadThread()
        {
            using (var _httpClient = new HttpClient())
                while (!AbortThreads)
                {
                    Thread.Sleep(sleepTime);
                    if (!DownloadQueue.TryDequeue(out var result))
                        continue;

                    try
                    {
                        DownloadAsync(result, _httpClient);
                    }
                    catch (Exception e)
                    {
                        MelonLoader.MelonLogger.Error($"Download failed! '{e.Message}' ");
                        result.FileWriteFailed = true;
                    }
                }
        }

        private static void ReadFromDiskThread()
        {
            while (!AbortThreads)
            {
                Thread.Sleep(sleepTime);
                if (!DiskReadQueue.TryDequeue(out var result) || result.FileReadFailed)
                    continue;

                try
                {
                    result.status = DownloadData.Status.LoadingFromFile;
                    result.rawData = File.ReadAllBytes(result.filePath);
                    result.FileReadDone = true;
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Error($"FileRead failed! '{e.Message}' ");
                    result.FileReadFailed = true;
                }
            }
        }
        private static void HashByteArrayThread()
        {
            byte[] hashBuffer = new byte[1048576];
            while (!AbortThreads)
            {
                Thread.Sleep(sleepTime);
                if (!HashQueue.TryDequeue(out var result) || result.HashFailed)
                    continue;
              
                try
                {
                    if (result?.rawData == null)
                    {
                        result.HashFailed = true;
                        result.FileReadFailed = true;
                        continue;
                    }
                    result.status = DownloadData.Status.Hashing;
                    using (var stream = new MemoryStream(result.rawData))
                    using (var md5Instance = System.Security.Cryptography.MD5.Create())
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        int bytesWritten;
                        do
                        {
                            bytesWritten = stream.Read(hashBuffer, 0, 1048576);
                            if (bytesWritten > 0)
                                md5Instance.TransformBlock(hashBuffer, 0, bytesWritten, null, 0);
                        }
                        while (bytesWritten > 0);
                        md5Instance.TransformFinalBlock(hashBuffer, 0, 0);
                        var hashResult = md5Instance.Hash;
                        result.calculatedHash = BitConverter.ToString(hashResult).Replace("-", "").ToLowerInvariant();
                        result.HashDone = true;
                    }
                   
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Error($"Hash calculation failed! '{e.Message}' ");
                    result.HashFailed = true;
                }
            }
        }
        private static void VerifyBundle(DownloadData downloadData)
        {
            using (BundleContext bundleContext = new BundleContext(downloadData.assetUrl, downloadData.decryptedData))
            {
                if (bundleContext.IsGoodUrl)
                {
                    downloadData.Verified = true;
                    return;
                }

                if (bundleContext.IsBadUrl || !bundleContext.PreProcessBytes() || !bundleContext.CompleteVerification())
                {
                    downloadData.decryptedData = null;
                    downloadData.Verified = false;
                    return;
                }
                downloadData.Verified = true;
            }
        }
        internal static bool ShouldVerify(DownloadData downloadData)
        {
            if (downloadData.type == DownloadTask.ObjectType.World || downloadData.isLocal)
            {
                downloadData.Verified = true;
                downloadData.VerifyDone = true;
                return false;
            }
            if (!MetaPort.Instance.settings.GetSettingsBool("ExperimentalBundleVerifierEnabled", false))
            {
                downloadData.Verified = true;
                downloadData.VerifyDone = true;
                return false;
            }
            // what is an enum even
            bool? userBundleVerifierBypass = MetaPort.Instance.SelfModerationManager.GetUserBundleVerifierBypass(downloadData.target);
            if (userBundleVerifierBypass != null)
            {
                return !userBundleVerifierBypass.Value;
            }

            bool friendsWith = MemoryCache.FriendsWith(downloadData.target);
            bool publicOnly = MetaPort.Instance.settings.GetSettingsBool("ExperimentalBundleVerifierPublicsOnly", false);
            bool filterFirends = MetaPort.Instance.settings.GetSettingsBool("ExperimentalBundleVerifierFilterFriends", false);
            bool isPublic = string.Equals(MetaPort.Instance.CurrentInstancePrivacy, "public", StringComparison.OrdinalIgnoreCase);

            var shouldVerify = (!publicOnly || isPublic) && (filterFirends || !friendsWith);
            if (!shouldVerify)
            {
                downloadData.Verified = true;
                downloadData.VerifyDone = true;
            }
            return shouldVerify;
        }
        private static void VerifyThread()
        {
            while (!AbortThreads)
            {
                Thread.Sleep(sleepTime);
                if (!VerifyQueue.TryDequeue(out var result) || result.FileReadFailed)
                    continue;

                try
                {
                    result.status = DownloadData.Status.BundleVerifier;
                    VerifyBundle(result);
                    result.VerifyDone = true;
                }
                catch (Exception e)
                {
                    result.Verified = false;
                    result.VerifyFailed = true;
                    MelonLoader.MelonLogger.Error($"Verify failed! '{e.Message}' ");
                }
            }
        }
        private static void StartNewThreads(string name, ThreadStart threadStart, int count = 1)
        {
            var sleep = new TimeSpan((long)(sleepTime * 10_000f / count));
            for (int i = 0; i < count; i++)
            {
                var thread = new Thread(threadStart)
                {
                    IsBackground = true,
                    Name = count == 1 ? $"[MC] {name}" : $"[MC] {name} {i + 1}"
                };
                threads.Add(thread);
                thread.Priority = ThreadPriority.BelowNormal;
                thread.Start();
                if (count > 1)
                    Thread.Sleep(sleep);
            }
        }
        
        internal enum FileCheck
        {
            ExistsSameVersion,
            ExistsDifferentVersion,
            NotExists
        }
        internal static IEnumerator Init(int downloadThreads = 5, int verifyThreads = 5)
        {
            downloadCountMax = downloadThreads;
            var threadCount = Environment.ProcessorCount > 10 ? 2 : 1;
            StartNewThreads("File write", WriteToDiskThread);
            StartNewThreads("Hash", HashByteArrayThread, threadCount);
            StartNewThreads("File read", ReadFromDiskThread, threadCount);
            StartNewThreads("Decrypt", DecryptThread);
            StartNewThreads("Verify", VerifyThread, verifyThreads);
            StartNewThreads("Download", DownloadThread, downloadThreads);
            downloadCounter.Release(downloadThreads);

            while (string.IsNullOrEmpty(MetaPort.Instance.APPLICATION_DATAPATH) || !Directory.Exists(MetaPort.Instance.APPLICATION_DATAPATH))
                yield return null;

            var dataPath = MetaPort.Instance.APPLICATION_DATAPATH;
            var AvatarsDir = dataPath + "/Avatars";
            var WorldsDir = dataPath + "/Worlds";
            var PropsDir = dataPath + "/Spawnables";

            var Avatars = Directory.CreateDirectory(AvatarsDir);
            var Worlds = Directory.CreateDirectory(WorldsDir);
            var Props = Directory.CreateDirectory(PropsDir);

            names[DownloadTask.ObjectType.Avatar] = (AvatarsDir, "cvravatar", Avatars);
            names[DownloadTask.ObjectType.World] = (WorldsDir, "cvrworld", Worlds);
            names[DownloadTask.ObjectType.Prop] = (PropsDir, "cvrprop", Props);

            for (int i = 0; i < 100; i++)
            {
                PercentageText[i] = $"Downloading {i} %";
            }

            StatusText[DownloadData.Status.None] = "Not started";
            StatusText[DownloadData.Status.NotStarted] = "Not started";
            StatusText[DownloadData.Status.DownloadQueued] = "Download queued";
            StatusText[DownloadData.Status.Downloading] = "Downloading";
            StatusText[DownloadData.Status.LoadingFromFileQueued] = "Loading file queued";
            StatusText[DownloadData.Status.LoadingFromFile] = "Loading file";
            StatusText[DownloadData.Status.HashingQueued] = "Hashing queued";
            StatusText[DownloadData.Status.Hashing] = "Hashing";
            StatusText[DownloadData.Status.DecryptingQueued] = "Decrypting queued";
            StatusText[DownloadData.Status.Decrypting] = "Decrypting";
            StatusText[DownloadData.Status.BundleVerifierQueued] = "BundleVerifier queued";
            StatusText[DownloadData.Status.BundleVerifier] = "BundleVerifier";
            StatusText[DownloadData.Status.LoadingBundleQueued] = "Loading bundle queued";
            StatusText[DownloadData.Status.LoadingBundle] = "Loading bundle";
            StatusText[DownloadData.Status.Done] = "Done";
            StatusText[DownloadData.Status.Error] = "Error";
            InitDone = true;
        }
    }
}