using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace ProceduralHands {
    /// <summary>
    /// Base compartida de <see cref="Hand"/>. Contiene las referencias, parámetros ajustables y el
    /// estado de agarre que usan los módulos de movimiento, posado y resaltado, además de algunos
    /// helpers de bajo nivel (ignorar colisiones, velocidad de lanzamiento). La lógica de
    /// agarrar/soltar en sí vive en <see cref="Hand"/>.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(HandFollower))]
    [RequireComponent(typeof(HandAnimator))]
    [RequireComponent(typeof(GrabbableHighlighter))]
    [DefaultExecutionOrder(10)]
    public class HandBase : MonoBehaviour {

        [Label("Dedos", "Los cinco componentes Finger que controla esta mano.")]
        public Finger[] fingers;

        [Label("Transform de la palma", "Empty en el centro de la palma; define el origen y la dirección de agarre.")]
        public Transform palmTransform;
        [Label("Transform de pinza", "Empty en el punto de pinza (pulgar/índice); se usa en los agarres de pinza.")]
        public Transform pinchPointTransform;

        [FormerlySerializedAs("isLeft")]
        [Label("Es la mano izquierda", "Marca si esta es la mano izquierda (on) o la derecha (off).")]
        public bool left = false;

        [Space]
        [Label("Distancia de alcance", "Distancia máxima a la que la mano puede agarrar/resaltar.", 0.01f)]
        public float reachDistance = 0.2f;

        [Header("Movimiento")]
        [Label("Habilitar movimiento", "Activa el movimiento físico (Rigidbody) hacia el objetivo de seguimiento.")]
        public bool enableMovement = true;
        [Label("Objetivo a seguir", "Transform que la mano intenta igualar con fuerzas físicas (normalmente el mando XR).")]
        public Transform follow;
        [Label("Offset de posición del seguimiento", "Desplazamiento fijo de la mano respecto al objetivo a seguir, en el espacio local del objetivo (se mueve y rota con el mando).")]
        public Vector3 followPositionOffset = Vector3.zero;
        [Label("Offset de rotación del seguimiento", "Rotación fija (grados) de la mano respecto al objetivo a seguir, en el espacio local del objetivo.")]
        public Vector3 followRotationOffset = Vector3.zero;
        [Label("Potencia de lanzamiento", "Multiplicador aplicado a la velocidad de lanzamiento al soltar.", 0f)]
        public float throwPower = 1.25f;
        [Label("Velocidad de retorno suave", "Con qué rapidez vuelve al mando el offset de un objeto agarrado con 'gentle grab'.", 0f)]
        public float gentleGrabSpeed = 1f;

        [Header("Posado (IK)")]
        [Label("Habilitar IK", "Activa el posado procedural de los dedos (sway, grip, poses de agarre).")]
        public bool enableIK = true;
        [Label("Fuerza del sway", "Cuánto se balancean los dedos según la velocidad de la mano.")]
        public float swayStrength = 0.4f;
        [Label("Offset de grip", "Flexión de reposo de los dedos (0 = abierto, 1 = cerrado).")]
        public float gripOffset = 0.14f;

        [Label("Pasos de flexión", "Número de pasos que da cada dedo al flexionarse para agarrar (más = contacto más fino).")]
        public int fingerBendSteps = 40;

        // Ajuste de la ventana de lanzamiento (avanzado; oculto en el inspector).
        [HideInInspector] public float throwVelocityExpireTime = 0.125f;
        [HideInInspector] public float throwAngularVelocityExpireTime = 0.25f;

        // --- Referencias a componentes (perezosas: se resuelven la primera vez que se piden) ---
        HandFollower _handFollow;
        public HandFollower handFollow => _handFollow != null ? _handFollow : (_handFollow = GetComponent<HandFollower>());

        HandAnimator _handAnimator;
        public HandAnimator handAnimator => _handAnimator != null ? _handAnimator : (_handAnimator = GetComponent<HandAnimator>());

        GrabbableHighlighter _highlighter;
        public GrabbableHighlighter highlighter => _highlighter != null ? _highlighter : (_highlighter = GetComponent<GrabbableHighlighter>());

        CollisionTracker _collisionTracker;
        public CollisionTracker collisionTracker {
            get {
                // Si no existe el tracker, intentamos obtenerlo y, si tampoco, lo añadimos.
                if (_collisionTracker == null && !TryGetComponent(out _collisionTracker))
                    _collisionTracker = gameObject.AddComponent<CollisionTracker>();
                return _collisionTracker;
            }
        }

        public HandVelocityTracker velocityTracker { get; protected set; }

        Rigidbody _body;
        public Rigidbody body => _body != null ? _body : (_body = GetComponent<Rigidbody>());

        public Transform moveTo => handFollow.moveTo;

        // --- Estado de agarre ---
        public Grabbable holdingObj { get; internal set; }

        protected GrabbablePose _currentHeldPose;
        public GrabbablePose currentHeldPose {
            get => _currentHeldPose;
            internal set {
                // Al pasar a null, avisamos a la pose anterior para que libere a esta mano (limpia posingHands).
                if (value == null && _currentHeldPose != null)
                    _currentHeldPose.CancelHandPose(this as Hand);
                _currentHeldPose = value;
            }
        }

        // El joint físico que conecta la mano con el objeto sostenido (no se serializa).
        [HideInInspector, System.NonSerialized] public ConfigurableJoint heldJoint;

        public bool grabbing { get; protected set; }   // true durante el proceso de agarre
        public bool squeezing { get; protected set; }  // true mientras el botón de squeeze está pulsado
        protected float gripAxis;    // valor analógico del grip (lo fija SetGrip)
        protected float squeezeAxis; // valor analógico del gatillo/squeeze (lo fija SetGrip)

        internal float lastGrabTime;
        internal float lastReleaseTime;

        // Todos los colliders bajo la jerarquía de la mano (para ignorar colisiones con lo agarrado).
        internal readonly List<Collider> handColliders = new List<Collider>();

        Transform _handGrabPoint;
        /// <summary>Transform que representa dónde queda la mano respecto al objeto sostenido.</summary>
        public Transform handGrabPoint {
            get {
                // Se crea perezosamente (solo si la escena ya está cargada para no crear objetos sueltos en build).
                if (_handGrabPoint == null && gameObject.scene.isLoaded)
                    _handGrabPoint = new GameObject("PH_GrabPoint").transform;
                return _handGrabPoint;
            }
        }

        Transform _localGrabbablePoint;
        /// <summary>Transform hijo que sigue dónde debería estar el objeto sostenido respecto a la mano.</summary>
        public Transform localGrabbablePoint {
            get {
                if (_localGrabbablePoint == null && gameObject.activeInHierarchy) {
                    _localGrabbablePoint = new GameObject("PH_GrabPosition").transform;
                    _localGrabbablePoint.parent = transform;
                }
                return _localGrabbablePoint;
            }
        }

        // Offsets de posición/rotación de agarre, para que la mano vuelva suavemente al mando tras agarrar.
        public Vector3 grabPositionOffset { get; set; } = Vector3.zero;
        public Quaternion grabRotationOffset { get; set; } = Quaternion.identity;

        protected Transform palmChild;     // hijo auxiliar de la palma usado por el AutoPose
        protected Collider palmCollider;   // caja (desactivada) de la palma usada por el AutoPose para ComputePenetration

        protected virtual void Awake() {
            // Configuramos el Rigidbody de la mano: sin gravedad, sin interpolación y con muchas iteraciones
            // de solver para que las articulaciones físicas (joints) sean estables.
            var rb = body;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.solverIterations = 100;
            rb.solverVelocityIterations = 100;

            // Creamos la caja de la palma para el AutoPose (desactivada: solo se enciende durante el cálculo).
            if (palmCollider == null && palmTransform != null) {
                var box = palmTransform.gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(0.2f, 0.15f, 0.05f);
                box.center = new Vector3(0f, 0f, -0.025f);
                box.enabled = false;
                palmCollider = box;
            }
            // Hijo auxiliar de la palma que el AutoPose usa para alinear la mano sin perder la orientación.
            if (palmChild == null && palmTransform != null) {
                palmChild = new GameObject("PH_PalmChild").transform;
                palmChild.parent = palmTransform;
            }

            // Creamos el rastreador de velocidad para el lanzamiento.
            if (velocityTracker == null)
                velocityTracker = new HandVelocityTracker(this);
        }

        protected virtual void OnEnable() {
            // Al activarse, recogemos todos los colliders de la mano (para la gestión de ignore-colisión).
            SetHandCollidersRecursive(transform);
        }

        protected virtual void OnDisable() {
            handColliders.Clear();
        }

        protected virtual void OnDestroy() {
            // Limpiamos los transforms auxiliares que creamos en runtime.
            if (_handGrabPoint != null)
                Destroy(_handGrabPoint.gameObject);
            if (_localGrabbablePoint != null)
                Destroy(_localGrabbablePoint.gameObject);
        }

        protected virtual void FixedUpdate() {
            // Cada paso de física actualizamos el registro de velocidad (para el throw) y avisamos al objeto sostenido.
            velocityTracker.UpdateThrowing();
            if (holdingObj != null)
                holdingObj.HeldFixedUpdate();
        }

        /// <summary>Recoge todos los colliders bajo la jerarquía de la mano para gestionar el ignore de colisiones.</summary>
        protected void SetHandCollidersRecursive(Transform obj) {
            handColliders.Clear();
            Add(obj);
            // Función local recursiva: añade los colliders de cada transform y baja a sus hijos.
            void Add(Transform t) {
                foreach (var col in t.GetComponents<Collider>())
                    handColliders.Add(col);
                for (int i = 0; i < t.childCount; i++)
                    Add(t.GetChild(i));
            }
        }

        /// <summary>Pone toda la jerarquía de <paramref name="root"/> en la capa <paramref name="layer"/>.</summary>
        protected void SetLayerRecursive(Transform root, int layer) {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
                SetLayerRecursive(root.GetChild(i), layer);
        }

        /// <summary>Activa/desactiva la colisión entre cada collider de la mano y <paramref name="other"/>.</summary>
        public void HandIgnoreCollider(Collider other, bool ignore) {
            for (int i = 0; i < handColliders.Count; i++)
                if (handColliders[i] != null && other != null)
                    Physics.IgnoreCollision(handColliders[i], other, ignore);
        }

        /// <summary>Velocidad de lanzamiento actual, del rastreador de velocidad.</summary>
        public Vector3 ThrowVelocity() => velocityTracker.ThrowVelocity();
        /// <summary>Velocidad angular de lanzamiento actual, del rastreador de velocidad.</summary>
        public Vector3 ThrowAngularVelocity() => velocityTracker.ThrowAngularVelocity();

        /// <summary>Número de objetos en contacto con la mano (y con el objeto sostenido, si lo hay).</summary>
        public int CollisionCount() {
            int count = collisionTracker.collisionObjects.Count;
            if (holdingObj != null)
                count += holdingObj.CollisionCount();
            return count;
        }

        /// <summary>True mientras hay un agarre en curso (entre la pulsación y la conexión de sujeción).</summary>
        public bool IsGrabbing() => grabbing;

        /// <summary>True mientras se sostiene un objeto.</summary>
        public bool IsHolding() => holdingObj != null;

        /// <summary>Máscara de capas combinada de las manos izquierda y derecha.</summary>
        public static int GetHandsLayerMask() => LayerMask.GetMask(Hand.rightHandLayerName, Hand.leftHandLayerName);

        protected virtual void OnDrawGizmosSelected() {
            // Dibuja la esfera de alcance saliendo de la palma hacia delante (para ajustar palma y alcance).
            if (palmTransform == null)
                return;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(palmTransform.position + palmTransform.forward * reachDistance, reachDistance);
        }
    }
}
