using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>Editor-only convenience: Space starts, Enter submits a sample answer.</summary>
    public class DevKeyboardDriver : MonoBehaviour
    {
        public InterviewSession Session;
        private int _answerNumber;

        private void Update()
        {
            if (!Session) return;
            if (Input.GetKeyDown(KeyCode.Space) && Session.State == SessionState.Idle)
                Session.StartInterview();
            if (Input.GetKeyDown(KeyCode.Return) && Session.State == SessionState.Listening)
            {
                _answerNumber++;
                Session.FinishAnswer($"모의 답변 {_answerNumber}입니다. 문제를 구조적으로 파악하고 우선순위를 정한 뒤 팀과 소통해 해결했습니다.");
            }
        }
    }
}
