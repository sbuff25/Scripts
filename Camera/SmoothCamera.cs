using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Mimics Unreal Engine 5's default viewport camera controls.
/// Uses the new Input System package.
///
/// Controls:
///   Right-Click + Mouse    — Look around (yaw/pitch)
///   Right-Click + WASD     — Fly forward/back/left/right
///   Right-Click + Q/E      — Fly down/up
///   Right-Click + Shift    — Speed boost
///   Middle-Click + Drag    — Pan (truck/pedestal)
///   Scroll Wheel           — Adjust move speed (while right-click held)
///                            or dolly forward/back (when not held)
/// </summary>
public class UE5StyleCamera : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Base movement speed in units/sec.")]
    [SerializeField] private float moveSpeed = 10f;

    [Tooltip("Minimum movement speed (scroll-wheel adjustable).")]
    [SerializeField] private float minSpeed = 1f;

    [Tooltip("Maximum movement speed (scroll-wheel adjustable).")]
    [SerializeField] private float maxSpeed = 100f;

    [Tooltip("Multiplier applied while holding Shift.")]
    [SerializeField] private float boostMultiplier = 3f;

    [Tooltip("How much the scroll wheel changes moveSpeed per tick.")]
    [SerializeField] private float speedScrollSensitivity = 0.5f;

    [Header("Look")]
    [Tooltip("Mouse look sensitivity.")]
    [SerializeField] private float lookSensitivity = 0.1f;

    [Tooltip("Clamp pitch to prevent flipping.")]
    [SerializeField] private float pitchClamp = 89f;

    [Header("Pan")]
    [Tooltip("Middle-mouse pan sensitivity.")]
    [SerializeField] private float panSensitivity = 0.02f;

    [Header("Dolly")]
    [Tooltip("Scroll-wheel dolly distance when right-click is NOT held.")]
    [SerializeField] private float dollySpeed = 5f;

    [Tooltip("Smoothing applied to dolly movement. 0 = instant.")]
    [SerializeField] private float dollySmoothTime = 0.1f;

    [Header("Smoothing")]
    [Tooltip("How quickly the camera eases into target velocity. Lower = snappier.")]
    [SerializeField] private float moveSmoothTime = 0.05f;

    // Internal state
    private float _yaw;
    private float _pitch;
    private Vector3 _currentVelocity;
    private Vector3 _velocitySmooth;
    private float _dollyRemaining;
    private float _dollyVelocity;

    // Input System device references
    private Mouse _mouse;
    private Keyboard _keyboard;

    private void Start()
    {
        Vector3 euler = transform.eulerAngles;
        _yaw = euler.y;
        _pitch = euler.x;
        // Normalize pitch into [-180, 180]
        if (_pitch > 180f) _pitch -= 360f;
    }

    private void Update()
    {
        // Re-fetch each frame — devices can connect/disconnect at runtime
        // See: https://docs.unity3d.com/Packages/com.unity.inputsystem@1.14/api/UnityEngine.InputSystem.Mouse.html
        _mouse = Mouse.current;
        _keyboard = Keyboard.current;
        if (_mouse == null || _keyboard == null) return;

        bool rightHeld = _mouse.rightButton.isPressed;
        bool middleHeld = _mouse.middleButton.isPressed;
        float scroll = _mouse.scroll.ReadValue().y;

        if (rightHeld)
        {
            HandleLook();
            HandleFlyMovement();
            HandleSpeedScroll(scroll);
            SetCursorLocked(true);
        }
        else if (middleHeld)
        {
            HandlePan();
            SetCursorLocked(true);
        }
        else
        {
            HandleDolly(scroll);
            SetCursorLocked(false);
            // Decay velocity when no input
            _currentVelocity = Vector3.SmoothDamp(
                _currentVelocity, Vector3.zero, ref _velocitySmooth, moveSmoothTime);
        }

        // Apply any residual smooth dolly
        ApplyDollySmooth();
    }

    private void HandleLook()
    {
        Vector2 delta = _mouse.delta.ReadValue();
        float mx = delta.x * lookSensitivity;
        float my = delta.y * lookSensitivity;

        _yaw += mx;
        _pitch -= my;
        _pitch = Mathf.Clamp(_pitch, -pitchClamp, pitchClamp);

        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    private void HandleFlyMovement()
    {
        if (_keyboard == null) return;

        Vector3 input = Vector3.zero;

        if (_keyboard.wKey.isPressed) input.z += 1f;
        if (_keyboard.sKey.isPressed) input.z -= 1f;
        if (_keyboard.aKey.isPressed) input.x -= 1f;
        if (_keyboard.dKey.isPressed) input.x += 1f;
        if (_keyboard.eKey.isPressed) input.y += 1f;
        if (_keyboard.qKey.isPressed) input.y -= 1f;

        if (input.sqrMagnitude > 1f)
            input.Normalize();

        float speed = moveSpeed;
        if (_keyboard.leftShiftKey.isPressed || _keyboard.rightShiftKey.isPressed)
            speed *= boostMultiplier;

        Vector3 targetVelocity = transform.TransformDirection(input) * speed;
        _currentVelocity = Vector3.SmoothDamp(
            _currentVelocity, targetVelocity, ref _velocitySmooth, moveSmoothTime);

        transform.position += _currentVelocity * Time.unscaledDeltaTime;
    }

    private void HandleSpeedScroll(float scroll)
    {
        if (Mathf.Abs(scroll) < 0.01f) return;
        // UE5 scales speed logarithmically with scroll
        float logSpeed = Mathf.Log(moveSpeed);
        logSpeed += Mathf.Sign(scroll) * speedScrollSensitivity;
        moveSpeed = Mathf.Clamp(Mathf.Exp(logSpeed), minSpeed, maxSpeed);
    }

    private void HandlePan()
    {
        Vector2 delta = _mouse.delta.ReadValue();
        float mx = -delta.x * panSensitivity;
        float my = -delta.y * panSensitivity;

        transform.position += transform.right * mx + transform.up * my;
    }

    private void HandleDolly(float scroll)
    {
        if (Mathf.Abs(scroll) < 0.01f) return;
        _dollyRemaining += Mathf.Sign(scroll) * dollySpeed;
    }

    private void ApplyDollySmooth()
    {
        if (Mathf.Abs(_dollyRemaining) < 0.0001f)
        {
            _dollyRemaining = 0f;
            return;
        }

        float step = Mathf.SmoothDamp(0f, _dollyRemaining, ref _dollyVelocity, dollySmoothTime);
        transform.position += transform.forward * step;
        _dollyRemaining -= step;
    }

    private void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
