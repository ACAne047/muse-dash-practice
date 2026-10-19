using Il2Cpp;
using Il2CppAssets.Scripts.GameCore.GameObjectLogics.GameObjectManager;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MuseDashPracticePrototype;

internal static class HoldEffectReset
{
    public static int ClearAndRearm()
    {
        LongActionController.PlayLongPressEffect(false, false);
        LongActionController.PlayLongPressEffect(false, true);
        var particles = new Dictionary<int, ParticleSystem>();
        Collect(LongActionController.s_GroundClipParticles);
        Collect(LongActionController.s_AirClipParticles);
        Collect(AttackEffectManager.instance?.m_ParticleLongPress);
        // Native hold-start only toggles emission. A stopped particle system
        // stays invisible when that flag is enabled again. Clear the old frame
        // while keeping the shared systems running with emission disabled.
        foreach (var particle in particles.Values)
        {
            var emission = particle.emission;
            emission.enabled = false;
            particle.Clear(false);
        }
        foreach (var particle in particles.Values)
            if (particle.gameObject.activeInHierarchy) particle.Play(false);
        return particles.Count;

        void Collect(Il2CppReferenceArray<ParticleSystem>? source)
        {
            if (source is null) return;
            foreach (var root in source)
            {
                if (!root) continue;
                foreach (var particle in root.GetComponentsInChildren<ParticleSystem>(true))
                    if (particle) particles[particle.GetInstanceID()] = particle;
            }
        }
    }
}
