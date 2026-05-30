using UnityEngine;
using System.Collections;

public class AvatarEventBridge : MonoBehaviour
{
    public static AvatarEventBridge Instance { get; private set; }

    [Header("Timing")]
    public float returnToIdleDelay = 0f; // 0 = immediately go back to Sleep when bot finishes

    private Coroutine returnCoroutine;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ── Appelle ces méthodes depuis ton bot IA ──

    public void OnUserStartedTyping()
    {
        CancelReturn();
        AvatarController.Instance?.GoThinking();
    }

    public void OnBotStartedResponding()
    {
        CancelReturn();
        AvatarController.Instance?.GoTalking();
    }

    public void OnGenerationStarted()
    {
        CancelReturn();
        AvatarController.Instance?.GoThinking();
    }

    public void OnGenerationCompleted()
    {
        CancelReturn();
        AvatarController.Instance?.GoTalking();
        // Don't auto-return here — wait for TTS audio to actually finish (OnBotFinished).
    }

    public void OnBotFinished()
    {
        CancelReturn();
        // Push-to-talk avatar flow: when TTS audio ends, return to Idle (visible, breathing).
        // Sleep state is kept available for a future "auto-sleep after N seconds idle" feature.
        AvatarController.Instance?.GoIdle();
    }

    public void OnAvatarSleep()
    {
        CancelReturn();
        AvatarController.Instance?.GoSleep();
    }

    public void OnAvatarWakeUp()
    {
        CancelReturn();
        AvatarController.Instance?.GoWakeUp();
        // Animator transitions Wake → Idle automatically when the Getting Up clip ends.
    }

    // ── Méthode de test ──
    [ContextMenu("Test Cycle")]
    public void TestCycle()
    {
        StartCoroutine(TestSequence());
    }

    IEnumerator TestSequence()
    {
        Debug.Log("[AvatarBridge] Test: Thinking...");
        OnUserStartedTyping();
        yield return new WaitForSeconds(2f);

        Debug.Log("[AvatarBridge] Test: Talking...");
        OnBotStartedResponding();
        yield return new WaitForSeconds(2f);

        Debug.Log("[AvatarBridge] Test: Back to Idle");
        OnBotFinished();
    }

    void CancelReturn()
    {
        if (returnCoroutine != null)
        {
            StopCoroutine(returnCoroutine);
            returnCoroutine = null;
        }
    }

    IEnumerator ReturnToIdleAfterDelay()
    {
        yield return new WaitForSeconds(returnToIdleDelay);
        AvatarController.Instance?.GoIdle();
    }
}