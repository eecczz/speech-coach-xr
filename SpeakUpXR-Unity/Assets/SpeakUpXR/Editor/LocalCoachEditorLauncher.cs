using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class LocalCoachEditorLauncher
{
    private const int CoachPort = 8002;

    static LocalCoachEditorLauncher()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
            StartIfNeeded();
    }

    [MenuItem("SpeakUpXR/Start Local Coach + TTS")]
    public static void StartIfNeeded()
    {
        if (IsListening()) return;

        string script = Path.GetFullPath(Path.Combine(Application.dataPath, "../../services/coach/run.ps1"));
        if (!File.Exists(script))
        {
            UnityEngine.Debug.LogError("[SpeakUpXR] Local coach launcher not found: " + script);
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Provider auto -Port {CoachPort}",
            WorkingDirectory = Path.GetDirectoryName(script),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(start);
        UnityEngine.Debug.Log("[SpeakUpXR] Starting local coach/TTS service on 127.0.0.1:8002. The entrance sequence gives it time to become ready.");
    }

    private static bool IsListening()
    {
        try
        {
            using var client = new TcpClient();
            var connection = client.BeginConnect("127.0.0.1", CoachPort, null, null);
            return connection.AsyncWaitHandle.WaitOne(120) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
