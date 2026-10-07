using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Limen.EditorBridge
{
    internal static class BridgeState
    {
        private const int MaximumLogs = 500;
        private const int MaximumJobs = 100;
        private static readonly object Gate = new object();
        private static readonly List<BridgeLogEntry> Logs = new List<BridgeLogEntry>();
        private static readonly List<BridgeJobRecord> Jobs = new List<BridgeJobRecord>();
        private static long nextLogId;
        private static string stateDirectory;
        private static string jobsPath;

        public static void Initialize(string directory)
        {
            stateDirectory = directory;
            jobsPath = Path.Combine(stateDirectory, "jobs.json");
            Directory.CreateDirectory(stateDirectory);
            LoadJobs();
            CompleteReloadJobs();
        }

        public static void RecordLog(string message, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                Logs.Add(new BridgeLogEntry
                {
                    id = ++nextLogId,
                    timestampUtc = DateTime.UtcNow.ToString("O"),
                    type = type.ToString(),
                    message = message ?? string.Empty,
                    stackTrace = stackTrace ?? string.Empty
                });
                if (Logs.Count > MaximumLogs) Logs.RemoveRange(0, Logs.Count - MaximumLogs);
            }
        }

        public static BridgeLogEntry[] GetLogs(long after, int limit, out long latestId)
        {
            lock (Gate)
            {
                latestId = nextLogId;
                int safeLimit = Math.Max(1, Math.Min(limit <= 0 ? 100 : limit, MaximumLogs));
                return Logs.Where(entry => entry.id > after).Take(safeLimit).ToArray();
            }
        }

        public static BridgeJobRecord CreateJob(string command)
        {
            lock (Gate)
            {
                string now = DateTime.UtcNow.ToString("O");
                var job = new BridgeJobRecord
                {
                    id = Guid.NewGuid().ToString("N"),
                    command = command,
                    state = "queued",
                    message = string.Empty,
                    createdUtc = now,
                    updatedUtc = now
                };
                Jobs.Insert(0, job);
                if (Jobs.Count > MaximumJobs) Jobs.RemoveRange(MaximumJobs, Jobs.Count - MaximumJobs);
                SaveJobsLocked();
                return Clone(job);
            }
        }

        public static BridgeJobRecord GetJob(string id)
        {
            lock (Gate)
            {
                BridgeJobRecord job = Jobs.FirstOrDefault(candidate => candidate.id == id);
                return job == null ? null : Clone(job);
            }
        }

        public static BridgeJobRecord GetLatestActiveJob(string command)
        {
            lock (Gate)
            {
                BridgeJobRecord job = Jobs.FirstOrDefault(candidate =>
                    candidate.command == command &&
                    (candidate.state == "queued" || candidate.state == "awaiting-domain-reload"));
                return job == null ? null : Clone(job);
            }
        }

        public static string GetLatestJobId(string command, string state)
        {
            lock (Gate)
            {
                return Jobs.FirstOrDefault(candidate => candidate.command == command && candidate.state == state)?.id;
            }
        }

        public static int UpdateJobs(string command, string fromState, string toState, string message)
        {
            lock (Gate)
            {
                string now = DateTime.UtcNow.ToString("O");
                int count = 0;
                foreach (BridgeJobRecord job in Jobs.Where(candidate =>
                             candidate.command == command && candidate.state == fromState))
                {
                    job.state = toState;
                    job.message = message ?? string.Empty;
                    job.updatedUtc = now;
                    count++;
                }
                if (count > 0) SaveJobsLocked();
                return count;
            }
        }

        public static void UpdateJob(string id, string state, string message)
        {
            lock (Gate)
            {
                BridgeJobRecord job = Jobs.FirstOrDefault(candidate => candidate.id == id);
                if (job == null) return;
                job.state = state;
                job.message = message ?? string.Empty;
                job.updatedUtc = DateTime.UtcNow.ToString("O");
                SaveJobsLocked();
            }
        }

        private static void LoadJobs()
        {
            lock (Gate)
            {
                Jobs.Clear();
                if (!File.Exists(jobsPath)) return;
                try
                {
                    BridgeJobFile file = JsonUtility.FromJson<BridgeJobFile>(File.ReadAllText(jobsPath));
                    if (file?.jobs != null) Jobs.AddRange(file.jobs.Where(job => job != null));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Limen Editor Bridge could not read its job journal: {exception.Message}");
                }
            }
        }

        private static void CompleteReloadJobs()
        {
            lock (Gate)
            {
                bool changed = false;
                foreach (BridgeJobRecord job in Jobs.Where(job => job.state == "awaiting-domain-reload"))
                {
                    job.state = "complete";
                    job.message = "Unity refreshed assets and completed the editor domain reload.";
                    job.updatedUtc = DateTime.UtcNow.ToString("O");
                    changed = true;
                }
                if (changed) SaveJobsLocked();
            }
        }

        private static void SaveJobsLocked()
        {
            if (string.IsNullOrEmpty(jobsPath)) return;
            Directory.CreateDirectory(stateDirectory);
            string temporaryPath = jobsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(new BridgeJobFile { jobs = Jobs.ToArray() }, true));
            if (File.Exists(jobsPath)) File.Delete(jobsPath);
            File.Move(temporaryPath, jobsPath);
        }

        private static BridgeJobRecord Clone(BridgeJobRecord source) => new BridgeJobRecord
        {
            id = source.id,
            command = source.command,
            state = source.state,
            message = source.message,
            createdUtc = source.createdUtc,
            updatedUtc = source.updatedUtc
        };
    }
}
