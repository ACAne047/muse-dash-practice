using Il2CppAssets.Scripts.GameCore.Managers;
using Il2CppAssets.Scripts.PeroTools.Managers;
using UnityEngine;

namespace MuseDashPracticePrototype;

internal static class CharacterPresentationReset
{
    public static object Restore()
    {
        var girl = GlobalManagers.girlManager ?? throw new InvalidOperationException("No character manager");
        var wasAir = girl.m_IsAir;
        var wasJumping = girl.m_IsJumpingAction;
        // The chart/input reset does not reset the separate character host.
        // Cancel its known hurt callback and invalidate old animation tracks.
        if (girl.hurtCoroutine is { } pending)
        {
            CoroutineManager.instance.StopCoroutine(ref pending);
            girl.hurtCoroutine = null;
        }
        girl.SetTone(false);
        girl.SetJumpingAction(false);
        girl.isThisFrameJumpUp = false;
        girl.isThisFrameJumpDown = false;
        girl.m_DownHitTime?.Clear();
        girl.m_MulHitTime = default;
        girl.m_MulHitHardTime = default;
        if (girl.m_Sac is { } action && action)
        {
            action.ClearTracks();
            action.m_CurrentSpineActionName = null;
            action.curActionKey = null;
        }
        girl.UnLockActionProtect();
        var animator = girl.m_CurAnimator;
        if (animator && animator.runtimeAnimatorController) animator.Rebind();

        // Use the selected character's movement behavior to select its neutral
        // animation, including characters whose normal pose already floats.
        // Do not run manager Init/Awake or register gameplay listeners again.
        girl.PlayLongPressAnimator(false, false);
        girl.PlayLongEffectSound(false);
        if (animator && animator.runtimeAnimatorController) animator.Update(0f);
        if (girl.m_IsAir || girl.m_IsJumpingAction)
            throw new InvalidOperationException("Character air/hold state was not reset");
        return new { wasAir, wasJumping, isAir = girl.m_IsAir, isJumping = girl.m_IsJumpingAction };
    }
}
