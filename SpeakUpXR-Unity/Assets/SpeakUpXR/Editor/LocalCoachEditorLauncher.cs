using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class LocalCoachEditorLauncher
{
    private const int CoachPort = 8002;
    private const int OllamaPort = 11434;

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
        // Port 8002 can remain alive while the actual Ollama inference process has
        // stopped. Treat the local AI as ready only when both services are reachable.
        if (IsListening(CoachPort) && IsListening(OllamaPort)) return;

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
        UnityEngine.Debug.Log(
            "[SpeakUpXR] Starting/checking Ollama on 127.0.0.1:11434 and coach/TTS on 127.0.0.1:8002.");
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connection = client.BeginConnect("127.0.0.1", port, null, null);
            return connection.AsyncWaitHandle.WaitOne(120) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
