using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM 
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM 
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour
    {
        [Header("Player")]
        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;

        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;

        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;

        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;

        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;

        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;

        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;

        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;

        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;

        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;

        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        [Header("Attack (optional)")]
        [Tooltip("Jika diisi, controller akan langsung cross-fade / play ke state animator ini (format: 'StateName' atau 'LayerName.StateName' tergantung animator).")]
        public string AttackStateName = ""; // kosong = gunakan parameter Animator (Trigger/Bool)

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;
        private int _animIDIsAttack; // fallback bool name "isAttack"
        private int _animIDAttackTrigger; // preferred trigger name "Attack"
        private bool _useAttackTrigger = false;

#if ENABLE_INPUT_SYSTEM 
        private PlayerInput _playerInput;
        private InputAction _attackAction;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private StarterAssetsInputs _input;
        private GameObject _mainCamera;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;

        // New: flag to block animator parameter updates while an attack animation is playing
        private bool _isAttacking = false;

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput != null && _playerInput.currentControlScheme == "KeyboardMouse";
#else
                return false;
#endif
            }
        }

        private void Awake()
        {
            if (_mainCamera == null)
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
            }
        }

        private void Start()
        {
            if (CinemachineCameraTarget != null)
                _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<StarterAssetsInputs>();

            AssignAnimationIDs();
            DetectAttackParameterType();

#if ENABLE_INPUT_SYSTEM 
            _playerInput = GetComponent<PlayerInput>();
            if (_playerInput != null && _playerInput.actions != null)
            {
                _attackAction = _playerInput.actions.FindAction("Attack");
                if (_attackAction != null)
                {
                    _attackAction.performed += OnAttackPerformed;
                }
            }
#else
            Debug.Log("New Input System not enabled - using legacy Input for Attack (T).");
#endif

            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;
        }

        private void OnDestroy()
        {
#if ENABLE_INPUT_SYSTEM
            if (_attackAction != null)
                _attackAction.performed -= OnAttackPerformed;
#endif
        }

        private void Update()
        {
            _hasAnimator = TryGetComponent(out _animator);

            JumpAndGravity();
            GroundedCheck();
            Move();

            // legacy fallback: allow T key even if new input system is used / not configured
            if (Input.GetKeyDown(KeyCode.T))
            {
                TriggerAttack();
            }
        }

        private void LateUpdate()
        {
            CameraRotation();
        }

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
            _animIDIsAttack = Animator.StringToHash("isAttack");
            _animIDAttackTrigger = Animator.StringToHash("Attack");
        }

        private void DetectAttackParameterType()
        {
            // pastikan _animator sudah ada
            if (_animator == null) _hasAnimator = TryGetComponent(out _animator);
            if (_animator == null) return;

            // prefer Trigger parameter named "Attack"
            foreach (var p in _animator.parameters)
            {
                if (p.name == "Attack" && p.type == AnimatorControllerParameterType.Trigger)
                {
                    _useAttackTrigger = true;
                    return;
                }
            }

            // fallback: check if "isAttack" bool exists
            foreach (var p in _animator.parameters)
            {
                if (p.name == "isAttack" && p.type == AnimatorControllerParameterType.Bool)
                {
                    _useAttackTrigger = false;
                    return;
                }
            }

            // default ke trigger
            _useAttackTrigger = true;
        }

#if ENABLE_INPUT_SYSTEM
        private void OnAttackPerformed(InputAction.CallbackContext ctx)
        {
            TriggerAttack();
        }
#endif

        private void TriggerAttack()
        {
            if (!_hasAnimator || _animator == null) return;

            if (_isAttacking) return; // block double-trigger

            // jika user menyetkan nama state attack, play langsung tanpa transisi panjang
            if (!string.IsNullOrEmpty(AttackStateName))
            {
                _isAttacking = true;
                _animator.Play(AttackStateName, 0, 0.0f);
                StartCoroutine(WaitForAttackEnd(resetBool:false));
                return;
            }

            if (_useAttackTrigger)
            {
                _isAttacking = true;
                _animator.ResetTrigger(_animIDAttackTrigger);
                _animator.SetTrigger(_animIDAttackTrigger);
                StartCoroutine(WaitForAttackEnd(resetBool:false));
            }
            else
            {
                // fallback bool: set true lalu tunggu sampai anim selesai baru reset
                _isAttacking = true;
                _animator.SetBool(_animIDIsAttack, true);
                StartCoroutine(WaitForAttackEnd(resetBool:true));
            }
        }

        private IEnumerator WaitForAttackEnd(bool resetBool)
        {
            // menunggu sampai animator selesai memainkan satu cycle state aktif di layer 0
            float timeout = 5f;
            float timer = 0f;

            // tunggu agar transition ke state attack terjadi
            yield return null;

            while (timer < timeout)
            {
                if (_animator == null) break;

                var st = _animator.GetCurrentAnimatorStateInfo(0);

                // jika state sedang dalam transition, terus tunggu
                if (_animator.IsInTransition(0))
                {
                    timer += Time.deltaTime;
                    yield return null;
                    continue;
                }

                // jika normalizedTime >= 1 => satu loop selesai; berhenti
                if (st.normalizedTime >= 1f)
                    break;

                timer += Time.deltaTime;
                yield return null;
            }

            // small extra buffer to ensure exit transitions can start
            yield return new WaitForSeconds(0.02f);

            if (resetBool && _animator != null)
                _animator.SetBool(_animIDIsAttack, false);

            _isAttacking = false;
        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset,
                transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            // Jangan update grounded parameter jika sedang menyerang (mencegah interupsi)
            if (_hasAnimator && _animator != null && !_isAttacking)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        private void CameraRotation()
        {
            if (CinemachineCameraTarget == null) return;

            if (_input != null && _input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
            {
                float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;

                _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
            }

            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride,
                _cinemachineTargetYaw, 0.0f);
        }

        private void Move()
        {
            if (_input == null || _controller == null || _mainCamera == null) return;

            float targetSpeed = _input.sprint ? SprintSpeed : MoveSpeed;

            if (_input.move == Vector2.zero) targetSpeed = 0.0f;

            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;

            float speedOffset = 0.1f;
            float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;

            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude,
                    Time.deltaTime * SpeedChangeRate);

                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

            if (_input.move != Vector2.zero)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg +
                                  _mainCamera.transform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity,
                    RotationSmoothTime);

                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }

            Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

            _controller.Move(targetDirection.normalized * (_speed * Time.deltaTime) +
                             new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            // Jangan update movement-related animator params selama attack berjalan
            if (_hasAnimator && _animator != null && !_isAttacking)
            {
                _animator.SetFloat(_animIDSpeed, _animationBlend);
                _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
            }
        }

        private void JumpAndGravity()
        {
            if (_controller == null || _input == null) return;

            if (Grounded)
            {
                _fallTimeoutDelta = FallTimeout;

                if (_hasAnimator && _animator != null && !_isAttacking)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                if (_verticalVelocity < 0.0f)
                {
                    _verticalVelocity = -2f;
                }

                if (_input.jump && _jumpTimeoutDelta <= 0.0f)
                {
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);

                    if (_hasAnimator && _animator != null && !_isAttacking)
                    {
                        _animator.SetBool(_animIDJump, true);
                    }
                }

                if (_jumpTimeoutDelta >= 0.0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                _jumpTimeoutDelta = JumpTimeout;

                if (_fallTimeoutDelta >= 0.0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    if (_hasAnimator && _animator != null && !_isAttacking)
                    {
                        _animator.SetBool(_animIDFreeFall, true);
                    }
                }

                _input.jump = false;
            }

            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += Gravity * Time.deltaTime;
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            Gizmos.color = Grounded ? transparentGreen : transparentRed;

            Gizmos.DrawSphere(
                new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z),
                GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                if (FootstepAudioClips != null && FootstepAudioClips.Length > 0 && _controller != null)
                {
                    var index = Random.Range(0, FootstepAudioClips.Length);
                    AudioSource.PlayClipAtPoint(FootstepAudioClips[index], transform.TransformPoint(_controller.center), FootstepAudioVolume);
                }
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f && _controller != null)
            {
                if (LandingAudioClip != null)
                    AudioSource.PlayClipAtPoint(LandingAudioClip, transform.TransformPoint(_controller.center), FootstepAudioVolume);
            }
        }
    }
}