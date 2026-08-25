using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>
    /// Author-time references for the interview setup placed inside the imported office building.
    /// Moving this object never creates characters or environment objects at runtime.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class NewOfficeInterviewLayout : MonoBehaviour
    {
        [Header("New office environment")]
        public GameObject OfficeBuilding;

        [Header("Interview anchors")]
        public InterviewEntranceSequence EntranceSequence;
        public Transform EntrancePoint;
        public Transform SeatPoint;
        public FirstPersonAvatarController Candidate;
        public InterviewerPanel Panel;

        [Header("Scene tuning")]
        [Min(0.7f)] public float InterviewerSpacing = 1.05f;
        [Min(1.2f)] public float PreferredCandidateDistance = 2.25f;
        public bool DrawLayoutGuides = true;

        private void OnDrawGizmosSelected()
        {
            if (!DrawLayoutGuides) return;
            if (EntrancePoint && SeatPoint)
            {
                Gizmos.color = new Color(0.2f, 0.75f, 1f, 0.9f);
                Gizmos.DrawLine(EntrancePoint.position + Vector3.up * 0.05f,
                    SeatPoint.position + Vector3.up * 0.05f);
                Gizmos.DrawWireSphere(EntrancePoint.position, 0.12f);
                Gizmos.DrawWireSphere(SeatPoint.position, 0.12f);
            }

            if (!Panel || Panel.Members == null || !SeatPoint) return;
            Gizmos.color = new Color(1f, 0.65f, 0.15f, 0.9f);
            foreach (var member in Panel.Members)
            {
                if (!member) continue;
                Transform visual = member.AvatarRoot ? member.AvatarRoot.transform : member.transform;
                Gizmos.DrawLine(visual.position + Vector3.up * 1.5f,
                    SeatPoint.position + Vector3.up * 1.5f);
            }
        }
    }
}
