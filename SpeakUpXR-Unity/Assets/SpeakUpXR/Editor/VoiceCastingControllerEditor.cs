using SpeakUpXR;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VoiceCastingController))]
public class VoiceCastingControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var casting = (VoiceCastingController)target;
        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("TTS 미리 듣기", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Play Mode에서 후보 버튼을 눌러 듣고, 위의 인덱스 3개를 정한 뒤 적용하세요.", MessageType.Info);

        DrawCandidates("남성 후보 (세 명 선택)", casting.MaleCandidates, casting.PreviewMale);

        if (GUILayout.Button("선택한 남성 음성 3개를 면접관에게 적용", GUILayout.Height(30f)))
        {
            casting.ApplySelection();
            if (!Application.isPlaying)
            {
                if (casting.Panel?.Members != null)
                    foreach (var member in casting.Panel.Members) if (member) EditorUtility.SetDirty(member);
                EditorUtility.SetDirty(casting);
            }
        }
    }

    private static void DrawCandidates(string title, VoiceCandidate[] candidates, System.Action<int> preview)
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        if (candidates == null) return;
        for (int i = 0; i < candidates.Length; i++)
        {
            string label = candidates[i]?.Label ?? $"후보 {i}";
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
                if (GUILayout.Button($"▶ [{i}] {label}")) preview(i);
        }
    }
}
