// 다대다 면접 패널 — 질문 kind에 따라 담당 면접관이 말하고, 나머지는 대기 자세를 유지한다.
// kind 매핑: base=인사, followup=직무, pressure=기술, closing=임원(마지막 멤버). 그 외는 인사.

using UnityEngine;

namespace SpeakUpXR
{
    public class InterviewerPanel : MonoBehaviour
    {
        public InterviewerController[] Members = new InterviewerController[0];

        public int IndexForKind(string kind)
        {
            int i = kind switch
            {
                "base" => 0,
                "followup" => 1,
                "pressure" => 2,
                "closing" => Members.Length - 1,
                _ => 0,
            };
            return Mathf.Clamp(i, 0, Mathf.Max(0, Members.Length - 1));
        }

        /// <summary>kind 담당 면접관만 입을 움직인다.</summary>
        public void BeginSpeaking(string kind)
        {
            int active = IndexForKind(kind);
            for (int i = 0; i < Members.Length; i++)
                if (Members[i]) Members[i].SetSpeaking(i == active);
        }

        public void EndSpeaking()
        {
            foreach (var m in Members)
                if (m) m.SetSpeaking(false);
        }

        /// <summary>답변을 들은 뒤 아무나 한 명이 끄덕인다.</summary>
        public void NodRandom()
        {
            if (Members.Length == 0) return;
            var m = Members[Random.Range(0, Members.Length)];
            if (m) m.Nod();
        }
    }
}
