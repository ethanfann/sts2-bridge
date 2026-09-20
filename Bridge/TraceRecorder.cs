using System;
using System.Globalization;
using System.IO;
using Godot;

namespace FirstMod.Bridge;

internal static class TraceRecorder
{
    private static readonly object Sync = new();

    private static string TraceFilePath => Path.Combine(StateExporter.StateDirectoryPath, "trace.log");

    public static void Log(string eventName, params (string Key, object? Value)[] fields)
    {
        try
        {
            Directory.CreateDirectory(StateExporter.StateDirectoryPath);
            string timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            string scene = ReadCurrentScene();
            string line = $"{timestamp} | {eventName} | scene={scene}";
            foreach ((string key, object? value) in fields)
            {
                line += $" | {key}={Format(value)}";
            }

            lock (Sync)
            {
                File.AppendAllText(TraceFilePath, line + System.Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static string ReadCurrentScene()
    {
        try
        {
            string statePath = StateExporter.StateFilePath;
            if (!File.Exists(statePath))
            {
                return "unknown";
            }

            string state = File.ReadAllText(statePath);
            const string token = "\"scene\": \"";
            int start = state.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
            {
                return "unknown";
            }

            start += token.Length;
            int end = state.IndexOf('"', start);
            return end > start ? state[start..end] : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Format(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is Node node)
        {
            return node.GetType().Name;
        }

        return value.ToString() ?? "null";
    }
}
