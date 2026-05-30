using UnityEngine;
using TMPro;

public class AvatarController : MonoBehaviour
{
    public static AvatarController Instance { get; private set; }

    [Header("UI References")]
    public Animator avatarAnimator;
    public TextMeshProUGUI statusText;

    [Header("State Colors")]
    public Color idleColor    = new Color(0.2f, 0.8f, 0.2f);
    public Color sleepColor   = new Color(0.3f, 0.3f, 0.8f);
    public Color thinkingColor= new Color(1.0f, 0.7f, 0.0f);
    public Color talkingColor = new Color(0.8f, 0.2f, 0.8f);
    public Color wakeupColor  = new Color(0.0f, 0.9f, 0.9f);

    public enum AvatarState { Idle = 0, Sleep = 1, WakeUp = 2, Thinking = 3, Talking = 4 }

    private AvatarState currentState;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        // Default at scene start: avatar stands idle (visible in HUD). Sleep state is
        // still available via GoSleep() — kept for a future "auto-sleep after idle" feature.
        SetState(AvatarState.Idle);
    }

    public void SetState(AvatarState newState)
    {
        currentState = newState;

        // Mettre à jour l'Animator
        if (avatarAnimator != null)
            avatarAnimator.SetInteger("AvatarState", (int)newState);

        // Mettre à jour le texte
        if (statusText != null)
        {
            statusText.text = newState.ToString();
            statusText.color = GetColorForState(newState);
        }

        Debug.Log($"[Avatar] State → {newState}");
    }

    Color GetColorForState(AvatarState state)
    {
        return state switch
        {
            AvatarState.Idle     => idleColor,
            AvatarState.Sleep    => sleepColor,
            AvatarState.WakeUp   => wakeupColor,
            AvatarState.Thinking => thinkingColor,
            AvatarState.Talking  => talkingColor,
            _                    => Color.white
        };
    }

    // Méthodes publiques appelables depuis n'importe où
    public void GoIdle()     => SetState(AvatarState.Idle);
    public void GoSleep()    => SetState(AvatarState.Sleep);
    public void GoWakeUp()   => SetState(AvatarState.WakeUp);
    public void GoThinking() => SetState(AvatarState.Thinking);
    public void GoTalking()  => SetState(AvatarState.Talking);

    public AvatarState GetCurrentState() => currentState;
}