using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Bridge.Bridge;

internal static class TraceRecorder
{
    private const long SegmentBytes = 16 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static string? _sessionDirectory;
    private static string? _lastStateId;
    private static long _sequence;
    private static long _segmentBytes;
    private static int _segment = 1;
    private static bool _disabled;

    public static void Start()
    {
        lock (Sync)
        {
            if (_sessionDirectory is not null || _disabled)
            {
                return;
            }

            try
            {
                string sessionId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
                _sessionDirectory = Path.Combine(StateExporter.StateDirectoryPath, "recordings", sessionId);
                Directory.CreateDirectory(_sessionDirectory);
                string releasePath = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "release_info.json");
                JsonElement? release = File.Exists(releasePath)
                    ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(releasePath)) : null;
                var metadata = new
                {
                    recording_version = 1,
                    session_id = sessionId,
                    started_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    game = release,
                    bridge_build = typeof(TraceRecorder).Assembly.ManifestModule.ModuleVersionId,
                    directory = _sessionDirectory,
                };
                string json = JsonSerializer.Serialize(metadata, JsonOptions);
                File.WriteAllText(Path.Combine(_sessionDirectory, "session.json"), json);
                string latest = Path.Combine(StateExporter.StateDirectoryPath, "latest-session.json");
                File.WriteAllText(latest + ".tmp", json);
                File.Move(latest + ".tmp", latest, true);
                Record("session_start", metadata);
                MegaCrit.Sts2.Core.Logging.Log.Warn($"STS2 Bridge recording session: {_sessionDirectory}");
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }
    }

    public static void RecordState(string json)
    {
        try
        {
            JsonElement state = JsonSerializer.Deserialize<JsonElement>(json);
            lock (Sync)
            {
                _lastStateId = state.GetProperty("state_id").GetString();
                Record("state", state);
            }
        }
        catch (Exception exception)
        {
            Disable(exception);
        }
    }

    public static void Log(string eventName, params (string Key, object? Value)[] fields)
    {
        // Never serialize a Godot/game object graph from a Harmony callback.
        // Hooks pass model IDs and scalar values; unknown objects become text.
        try
        {
            Dictionary<string, object?> values = new();
            foreach ((string key, object? value) in fields)
            {
                values[key] = value switch
                {
                    null or bool or int or long or uint or ulong or float or double or decimal => value,
                    Node node => node.GetType().Name,
                    _ => value.ToString(),
                };
            }
            Record("event", new { name = eventName, fields = values });
        }
        catch (Exception exception)
        {
            Disable(exception);
        }
    }

    public static bool Record(string type, object? data)
    {
        lock (Sync)
        {
            if (_disabled)
            {
                return false;
            }
            Start();
            if (_disabled || _sessionDirectory is null)
            {
                return false;
            }

            try
            {
                string line = JsonSerializer.Serialize(new
                {
                    sequence = ++_sequence,
                    timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    type,
                    // This is the last exported observation, not a claim that
                    // an asynchronous action has completed in that state.
                    observed_state_id = _lastStateId,
                    data,
                }, JsonOptions) + "\n";
                int bytes = Encoding.UTF8.GetByteCount(line);
                if (_segmentBytes > 0 && _segmentBytes + bytes > SegmentBytes)
                {
                    _segment++;
                    _segmentBytes = 0;
                }
                File.AppendAllText(Path.Combine(_sessionDirectory, $"timeline-{_segment:D4}.jsonl"), line);
                _segmentBytes += bytes;
                return true;
            }
            catch (Exception exception)
            {
                Disable(exception);
                return false;
            }
        }
    }

    private static void Disable(Exception exception)
    {
        if (!_disabled)
        {
            _disabled = true;
            MegaCrit.Sts2.Core.Logging.Log.Warn($"STS2 Bridge recording disabled after an I/O/serialization failure; gameplay continues: {exception.Message}");
        }
    }
}
