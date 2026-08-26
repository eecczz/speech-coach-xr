using UnityEngine;

namespace SpeakUpXR
{
    /// <summary>Reduces imported character gloss without modifying environment materials.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class InterviewerMaterialTuner : MonoBehaviour
    {
        public InterviewerPanel Panel;
        [Range(0f, 1f)] public float Smoothness = 0.2f;
        [Range(0f, 1f)] public float Metallic;
        [Range(0f, 1f)] public float SpecularLevel = 0.28f;
        public bool DisableClearCoat = true;

        private MaterialPropertyBlock _block;

        private void OnEnable() => ApplyNow();
        private void OnValidate() => ApplyNow();

        public void ApplyNow()
        {
            if (!Panel) Panel = GetComponent<InterviewerPanel>();
            if (!Panel || Panel.Members == null) return;
            _block ??= new MaterialPropertyBlock();
            foreach (InterviewerController member in Panel.Members)
            {
                if (!member || !member.AvatarRoot || !member.AvatarRoot.activeInHierarchy) continue;
                foreach (Renderer renderer in member.AvatarRoot.GetComponentsInChildren<Renderer>(true))
                {
                    int materialCount = Mathf.Max(1, renderer.sharedMaterials.Length);
                    for (int index = 0; index < materialCount; index++)
                    {
                        renderer.GetPropertyBlock(_block, index);
                        _block.SetFloat("_Smoothness", Smoothness);
                        _block.SetFloat("_Glossiness", Smoothness);
                        _block.SetFloat("_GlossMapScale", Smoothness);
                        _block.SetFloat("_Metallic", Metallic);
                        _block.SetColor("_SpecColor", Color.white * SpecularLevel);
                        if (DisableClearCoat)
                        {
                            _block.SetFloat("_ClearCoatMask", 0f);
                            _block.SetFloat("_CoatMask", 0f);
                        }
                        renderer.SetPropertyBlock(_block, index);
                        _block.Clear();
                    }
                }
            }
        }
    }
}
