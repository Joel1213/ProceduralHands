using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Base compartida de <see cref="Grabbable"/>. Gestiona la contabilidad de rigidbody/colliders: la
    /// lista de colliders de agarre, las capas y materiales físicos originales, el
    /// <see cref="GrabbablePoseCombiner"/> autoañadido, y las rutinas de ignorar colisión con la mano
    /// durante y después de un agarre. (Alcance a una mano: sin promediado multi-mano ni continuidad de
    /// cuerpos enlazados.)
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GrabbableBase : MonoBehaviour {

        [Label("Rigidbody", "El Rigidbody al que se conecta este grabbable. Por defecto, el de este GameObject.")]
        public Rigidbody body;

        [Label("Material de resaltado", "Material aplicado a una copia ligeramente agrandada del mesh para crear el efecto de resaltado.")]
        public Material highlightMaterial;

        [HideInInspector] public bool isGrabbable = true;

        /// <summary>El place point con el que este grabbable está asociado ahora mismo, si lo hay.</summary>
        public PlacePoint placePoint { get; protected set; }

        // Colliders no-trigger del objeto (los que se pueden agarrar).
        internal readonly List<Collider> grabColliders = new List<Collider>();
        public List<Collider> GrabColliders => grabColliders;

        // Valores de rigidbody cuando NO está sostenido (se restauran al soltar).
        public float targetMass { get; protected set; }
        public float targetDrag { get; protected set; }
        public float targetAngularDrag { get; protected set; }

        // Materiales y capas originales de cada collider, para restaurarlos tras un agarre.
        protected readonly Dictionary<Collider, PhysicsMaterial> grabColliderMaterials = new Dictionary<Collider, PhysicsMaterial>();
        protected readonly Dictionary<Transform, int> originalLayers = new Dictionary<Transform, int>();

        readonly List<Hand> _heldBy = new List<Hand>();
        public List<Hand> heldBy => _heldBy;                 // manos que lo sostienen
        protected readonly List<Hand> beingGrabbedBy = new List<Hand>(); // manos a media animación de agarre

        protected bool highlighting; // si está resaltado ahora mismo
        // Copias de mesh creadas para el resaltado, agrupadas por material.
        protected readonly Dictionary<Material, List<GameObject>> highlightObjs = new Dictionary<Material, List<GameObject>>();

        public Transform originalParent { get; set; } // padre original (para restaurar al soltar si parentamos)
        protected CollisionDetectionMode detectionMode; // modo de detección de colisión original del body
        protected RigidbodyInterpolation startInterpolation; // interpolación original del body

        public bool beingGrabbed { get; protected internal set; } // true durante la animación de agarre
        protected bool beingDestroyed; // evita lanzar rutinas mientras el objeto se destruye

        protected GrabbablePoseCombiner poseCombiner; // combina las poses guardadas de este grabbable

        // Estado de "ignorar colisión con la mano" y sus rutinas asociadas.
        protected readonly Dictionary<Hand, bool> ignoringHand = new Dictionary<Hand, bool>();
        protected readonly Dictionary<Hand, Coroutine> ignoreRoutines = new Dictionary<Hand, Coroutine>();

        CollisionTracker _collisionTracker;
        public CollisionTracker collisionTracker {
            get {
                // Lo obtenemos o lo añadimos; en grabbables solo rastreamos colisiones, no triggers.
                if (_collisionTracker == null && !TryGetComponent(out _collisionTracker)) {
                    _collisionTracker = gameObject.AddComponent<CollisionTracker>();
                    _collisionTracker.disableTriggersTracking = true;
                }
                return _collisionTracker;
            }
        }

        /// <summary>El transform del rigidbody raíz (seguro incluso si el body falta temporalmente).</summary>
        public Transform rootTransform {
            get {
                if (body != null)
                    return body.transform;
                var rb = GetComponentInParent<Rigidbody>();
                return rb != null ? rb.transform : transform;
            }
        }

        public virtual void Awake() {
            // Aseguramos un combiner de poses y recogemos todas las GrabbablePose de la jerarquía.
            if (!TryGetComponent(out poseCombiner))
                poseCombiner = gameObject.AddComponent<GrabbablePoseCombiner>();

            CollectPoses(transform);

            // Resolvemos el Rigidbody.
            if (body == null && !TryGetComponent(out body))
                Debug.LogError("PROCEDURAL HANDS: el Grabbable no tiene Rigidbody.", this);

            // Guardamos el estado original del body y recogemos colliders/ajustes.
            if (body != null) {
                originalParent = body.transform.parent;
                detectionMode = body.collisionDetectionMode;
                startInterpolation = body.interpolation;
                UpdateGrabbableColliderSettings();
                UpdateGrabbableRigidbodySettings(body.linearDamping, body.angularDamping, body.mass);
            }
        }

        // Recorre la jerarquía recogiendo GrabbablePose (sin entrar en otros grabbables anidados).
        void CollectPoses(Transform obj) {
            if (obj != transform && obj.TryGetComponent(out Grabbable other) && other != this)
                return;
            foreach (var pose in obj.GetComponents<GrabbablePose>()) {
                poseCombiner.AddPose(pose);
                pose.grabbable = this as Grabbable;
            }
            for (int i = 0; i < obj.childCount; i++)
                CollectPoses(obj.GetChild(i));
        }

        protected virtual void OnDestroy() {
            beingDestroyed = true;
        }

        public virtual void HeldFixedUpdate() {
            // Si dejó de ser agarrable mientras lo sostenían, lo soltamos a la fuerza.
            if (heldBy.Count > 0 && (!isGrabbable || !enabled))
                ForceHandsRelease();
        }

        /// <summary>Asegura que la referencia al rigidbody es válida (en el alcance a una mano nunca se "desactiva" de verdad).</summary>
        public void ActivateRigidbody() {
            if (body == null)
                TryGetComponent(out body);
        }

        /// <summary>Número de objetos en contacto con este grabbable.</summary>
        public int CollisionCount() => collisionTracker.collisionObjects.Count;

        //=================================================================
        //========================= CAPAS =================================
        //=================================================================

        /// <summary>Pone cada transform de los colliders de agarre en <paramref name="newLayer"/>.</summary>
        protected internal void SetLayerRecursive(int newLayer) {
            foreach (var kvp in originalLayers)
                if (kvp.Key != null)
                    kvp.Key.gameObject.layer = newLayer;
        }

        /// <summary>Restaura las capas que tenían los colliders de agarre al iniciar.</summary>
        protected internal void ResetOriginalLayers() {
            foreach (var kvp in originalLayers)
                if (kvp.Key != null)
                    kvp.Key.gameObject.layer = kvp.Value;
        }

        /// <summary>Reconstruye la lista de colliders de agarre, sus capas y materiales originales. No llamar mientras se sostiene.</summary>
        public void UpdateGrabbableColliderSettings() {
            grabColliders.Clear();
            grabColliderMaterials.Clear();
            originalLayers.Clear();
            if (body == null)
                return;

            // Recogemos todos los colliders no-trigger bajo el body.
            foreach (var col in body.GetComponentsInChildren<Collider>()) {
                if (col.isTrigger)
                    continue;
                grabColliders.Add(col);
                grabColliderMaterials[col] = col.sharedMaterial;

                if (!originalLayers.ContainsKey(col.transform)) {
                    // Si el collider está en la capa Default (sin asignar), lo pasamos a la capa Grabbable.
                    int grabbable = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);
                    if (grabbable >= 0 && (col.gameObject.layer == 0 || LayerMask.LayerToName(col.gameObject.layer) == ""))
                        col.gameObject.layer = grabbable;
                    originalLayers[col.transform] = col.gameObject.layer;
                }
            }
        }

        /// <summary>Guarda el drag/masa objetivo del body cuando NO se sostiene, aplicándolos si no está sostenido.</summary>
        public void UpdateGrabbableRigidbodySettings(float drag, float angularDrag, float mass) {
            targetDrag = drag;
            targetAngularDrag = angularDrag;
            targetMass = mass;
            if (body != null && heldBy.Count == 0) {
                body.linearDamping = drag;
                body.angularDamping = angularDrag;
                body.mass = mass;
            }
        }

        /// <summary>Pone el material físico en todos los colliders de agarre.</summary>
        public void SetPhysicMaterial(PhysicsMaterial material) {
            foreach (var col in grabColliders)
                if (col != null)
                    col.sharedMaterial = material;
        }

        /// <summary>Restaura los materiales físicos originales de los colliders de agarre.</summary>
        public void ResetPhysicsMaterial() {
            foreach (var kvp in grabColliderMaterials)
                if (kvp.Key != null)
                    kvp.Key.sharedMaterial = kvp.Value;
        }

        //=================================================================
        //=================== IGNORAR COLISIONES ==========================
        //=================================================================

        /// <summary>Activa/desactiva la colisión entre los colliders de este grabbable y una mano.</summary>
        public void IgnoreHand(Hand hand, bool ignore) {
            foreach (var col in grabColliders)
                hand.HandIgnoreCollider(col, ignore);
            ignoringHand[hand] = ignore;
        }

        /// <summary>Ignora la colisión con la mano durante un tiempo fijo (en la aproximación del agarre).</summary>
        public void IgnoreHandCollision(Hand hand, float time) {
            if (gameObject.activeInHierarchy && !beingDestroyed)
                StartTrackedRoutine(hand, IgnoreHandCollisionRoutine(hand, time));
        }

        /// <summary>Ignora la colisión con la mano hasta que dejen de solaparse (tras soltar).</summary>
        public void IgnoreHandCollisionUntilNone(Hand hand, float minTime) {
            if (gameObject.activeInHierarchy && !beingDestroyed)
                StartTrackedRoutine(hand, IgnoreHandCollisionUntilNoneRoutine(hand, minTime));
        }

        // Inicia una rutina de ignore para una mano, cancelando la anterior si la hubiera.
        void StartTrackedRoutine(Hand hand, IEnumerator routine) {
            if (ignoreRoutines.TryGetValue(hand, out var existing) && existing != null)
                StopCoroutine(existing);
            ignoreRoutines[hand] = StartCoroutine(routine);
        }

        /// <summary>Detiene cualquier rutina de ignore en curso para la mano dada (para que no auto-restaure).</summary>
        protected void StopIgnoreRoutine(Hand hand) {
            if (ignoreRoutines.TryGetValue(hand, out var existing) && existing != null)
                StopCoroutine(existing);
            ignoreRoutines.Remove(hand);
        }

        // Ignora un tiempo fijo y luego restaura.
        IEnumerator IgnoreHandCollisionRoutine(Hand hand, float time) {
            IgnoreHand(hand, true);
            yield return new WaitForSeconds(time);
            IgnoreHand(hand, false);
            ignoreRoutines.Remove(hand);
        }

        // Ignora un mínimo de tiempo y luego espera a que mano y objeto dejen de solaparse antes de restaurar.
        IEnumerator IgnoreHandCollisionUntilNoneRoutine(Hand hand, float minTime) {
            IgnoreHand(hand, true);
            yield return new WaitForSeconds(minTime);
            while (IsHandOverlapping(hand))
                yield return new WaitForSeconds(0.1f);
            IgnoreHand(hand, false);
            ignoreRoutines.Remove(hand);
        }

        /// <summary>True si algún collider sólido de este grabbable solapa con algún collider de la mano.</summary>
        public bool IsHandOverlapping(Hand hand) {
            foreach (var col2 in grabColliders) {
                if (col2 == null || !col2.enabled || col2.isTrigger)
                    continue;
                foreach (var col1 in hand.handColliders) {
                    if (col1 == null || !col1.enabled || col1.isTrigger)
                        continue;
                    if (Physics.ComputePenetration(col1, col1.transform.position, col1.transform.rotation,
                            col2, col2.transform.position, col2.transform.rotation, out _, out _))
                        return true;
                }
            }
            return false;
        }

        //=================================================================
        //========================= POSES =================================
        //=================================================================

        /// <summary>Devuelve el combiner de poses si este grabbable tiene alguna pose guardada.</summary>
        public bool GetSavedPose(out GrabbablePoseCombiner combiner) {
            if (poseCombiner != null && poseCombiner.PoseCount() > 0) {
                combiner = poseCombiner;
                return true;
            }
            combiner = null;
            return false;
        }

        /// <summary>Indica si este grabbable tiene alguna pose personalizada.</summary>
        public bool HasCustomPose() => poseCombiner != null && poseCombiner.PoseCount() > 0;

        /// <summary>Asocia este grabbable a un place point.</summary>
        public void SetPlacePoint(PlacePoint point) {
            placePoint = point;
        }

        // Lo implementa Grabbable.
        public virtual void ForceHandsRelease() { }
    }
}
