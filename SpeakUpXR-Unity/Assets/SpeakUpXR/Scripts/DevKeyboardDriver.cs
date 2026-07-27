// Editor/desktop convenience driver — Space starts the interview, Enter submits a canned
// answer while Listening. Lets the interview loop run end-to-end before XR controller
// input and STT are wired. Harmless on device (no keyboard events).

using UnityEngine;

namespace SpeakUpXR
{
    public class DevKeyboardDriver : MonoBehaviour
    {
        public InterviewSession Session;
        private int _n;

        private void Update()
        {
            if (Session == null) return;
            if (Input.GetKeyDown(KeyCode.Space) && Session.State == SessionState.Idle)
                Session.StartInterview();
            if (Input.GetKeyDown(KeyCode.Return) && Session.State == SessionState.Listening)
                Session.FinishAnswer($"(개발용 모의 답변 {++_n}) 저는 문제를 구조적으로 파악하고 우선순위를 정해 실행하는 편입니다.");
        }
    }
}
