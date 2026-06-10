using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Controla un único dedo de la mano. Se coloca en el nudillo del dedo y se le asignan las
    /// articulaciones (nudillo, media, distal) y un marcador de punta. Guarda las poses
    /// abierta/cerrada (y opcionalmente de pinza) e implementa la flexión procedural que se usa al
    /// auto-posar un agarre: el dedo interpola de una pose hacia otra mientras lanza una esfera de
    /// comprobación (spherecast) en la punta hasta que toca el objeto que se está agarrando.
    ///
    /// Cómo se usa: lo añade el wizard (un Finger por nudillo). En tiempo de ejecución, el
    /// <see cref="HandAnimator"/> y el <see cref="Hand"/> llaman a sus métodos para posar el dedo;
    /// en el editor, sus poses se guardan con los botones del inspector de la mano y el Hand Pose Tool.
    /// </summary>
    public class Finger : MonoBehaviour {

        [Header("Referencia de la mano")]
        [Label("Mano", "La mano a la que pertenece este dedo. Se resuelve sola desde el padre si se deja vacío.")]
        public Hand hand;

        [Header("Articulaciones del dedo")]
        [Label("Tipo de dedo", "Qué dedo es (índice, medio, anular, meñique o pulgar). Necesario para indexar sus poses.")]
        public FingerEnum fingerType = FingerEnum.none;

        [Label("Nudillo (knuckle)", "Primera articulación, la que rota todo el dedo. En el pulgar, la más cercana a la muñeca.")]
        public Transform knuckleJoint;
        [Label("Articulación media", "Segunda articulación, conecta la falange proximal con la media.")]
        public Transform middleJoint;
        [Label("Articulación distal", "Tercera articulación, conecta la falange media con la distal.")]
        public Transform distalJoint;

        [Space, Header("Punta del dedo")]
        [Label("Punta (tip)", "Marcador en la yema del dedo; se usa como sonda de contacto al flexionar.")]
        public Transform tip;
        [Label("Radio de la punta", "Radio de la esfera de comprobación en la punta al flexionar el dedo.", 0f)]
        public float tipRadius = 0.01f;

        [Label("Offset de flexión", "Desplaza la flexión de reposo del dedo (0 = abierto, 1 = cerrado).", 0f, 1f)]
        public float bendOffset;

        [Label("Velocidad de suavizado", "Con qué rapidez el dedo se acerca a su pose objetivo.", 0f)]
        public float fingerSmoothSpeed = 1f;

        /// <summary>Flexión extra en tiempo de ejecución que se suma a <see cref="bendOffset"/> (la fijan los finger benders).</summary>
        [HideInInspector] public float secondaryOffset = 0f;

        /// <summary>Poses guardadas, indexadas por <see cref="FingerPoseEnum"/> (Open, Closed, PinchOpen, PinchClosed).</summary>
        [SerializeField, HideInInspector]
        public FingerPoseData[] poseData;

        // Caché de las cuatro articulaciones (nudillo, media, distal, punta) para no reconstruir el array.
        [SerializeField, HideInInspector]
        Transform[] fingerJoints;

        // Pose reutilizable sin reservar memoria cada frame al flexionar/posar el dedo.
        FingerPoseData _poseDataNonAlloc;
        // Flexión actual del dedo (0..1). Solo informativa internamente.
        float bend;
        // Flexión alcanzada en la última llamada a BendFingerUntilHit (la usa la animación de agarre).
        float lastHitBend;
        // Buffer reutilizable para los resultados del spherecast (evita reservar memoria cada comprobación).
        readonly Collider[] results = new Collider[8];

        /// <summary>Las cuatro articulaciones en orden: nudillo, media, distal y punta.</summary>
        public Transform[] FingerJoints {
            get {
                // Si el array aún no existe o tiene un tamaño inesperado, lo (re)construimos a partir de las refs.
                if (fingerJoints == null || fingerJoints.Length != 4)
                    fingerJoints = new[] { knuckleJoint, middleJoint, distalJoint, tip };
                return fingerJoints;
            }
        }

        /// <summary>True si falta por asignar alguna articulación o la punta.</summary>
        // Una sola condición OR: basta con que cualquiera de las cuatro referencias sea null.
        public bool isMissingReferences => knuckleJoint == null || middleJoint == null || distalJoint == null || tip == null;

        // Garantiza que la pose reutilizable tiene sus arrays creados antes de usarla.
        void EnsureNonAlloc() => _poseDataNonAlloc.Allocate();

        protected virtual void Awake() {
            // Si nadie asignó la mano en el inspector, la buscamos hacia arriba en la jerarquía.
            if (hand == null)
                hand = GetComponentInParent<Hand>();
            // Si aún así no hay mano, el dedo no puede funcionar: avisamos.
            if (hand == null)
                Debug.LogError("PROCEDURAL HANDS: el Finger no tiene referencia a su mano.", this);

            // Sin las articulaciones no se puede posar ni flexionar: avisamos.
            if (isMissingReferences)
                Debug.LogError("PROCEDURAL HANDS: al Finger le faltan articulaciones; asigna nudillo, media, distal y punta.", this);

            // Las poses se serializan como matrices relativas, pero las rotaciones locales cacheadas NO
            // siempre sobreviven la serialización: las recalculamos aquí, una vez, para cada pose guardada.
            if (poseData != null)
                for (int i = 0; i < poseData.Length; i++)
                    if (poseData[i].isSet) // solo las que realmente tienen datos
                        poseData[i].CalculateAdditionalValues(hand.transform.lossyScale);
        }

        //=================================================================
        //========================= FLEXIÓN ===============================
        //=================================================================

        /// <summary>
        /// Flexiona el dedo desde una pose guardada hacia otra y se detiene cuando la punta toca por
        /// primera vez un collider de la máscara <paramref name="layermask"/>. Es la base del auto-pose.
        /// </summary>
        /// <param name="steps">Número de pasos de interpolación/comprobación física de 0 a 1.</param>
        /// <returns>True si encontró una flexión de contacto; false si llegó a la pose cerrada sin tocar.</returns>
        public virtual bool BendFingerUntilHit(int steps, int layermask, FingerPoseEnum fromPose = FingerPoseEnum.Open, FingerPoseEnum toPose = FingerPoseEnum.Closed) {
            // Si alguna de las poses pedidas no está guardada (p. ej. pinza sin autorar), sus datos están vacíos:
            // devolvemos false sin tocar el dedo para que quien llama use el agarre normal como respaldo (evita el IndexOutOfRange).
            if (!IsPoseSaved(fromPose) || !IsPoseSaved(toPose))
                return false;
            // Sobrecarga cómoda: convierte los enum de pose en referencias a los datos y llama a la versión real.
            return BendFingerUntilHit(steps, layermask, ref poseData[(int)fromPose], ref poseData[(int)toPose]);
        }

        /// <summary>Flexiona el dedo de <paramref name="fromPose"/> hacia <paramref name="toPose"/> y para en el primer contacto de la punta.</summary>
        public virtual bool BendFingerUntilHit(int steps, int layermask, ref FingerPoseData fromPose, ref FingerPoseData toPose) {
            // Reiniciamos la flexión de contacto que vamos a calcular.
            lastHitBend = 0;
            // Cacheamos la rotación de la mano y el transform de la punta (los usamos en cada paso).
            var handRotation = hand.transform.rotation;
            var tipTransform = tip;

            // --- PRIMERA PASADA (GRUESA) ---
            // coarseSteps = steps/5 (con steps=40 → 8 pasos). Recorremos la flexión a saltos grandes para
            // localizar rápido el tramo donde el dedo empieza a tocar, sin gastar 40 comprobaciones físicas.
            float coarseSteps = steps / 5f;
            for (float i = 0; i <= coarseSteps; i++) {
                // Convertimos el índice del paso en una flexión 0..1 (0 = abierto, 1 = cerrado).
                lastHitBend = i / coarseSteps;
                // Posamos el dedo en esa flexión y contamos cuántos colliders toca la punta.
                int overlapCount = CheckFingerBlendOverlap(layermask, handRotation, tipTransform, ref fromPose, ref toPose, lastHitBend);

                // ¿La punta ya toca algo en este paso?
                if (overlapCount > 0) {
                    // Aseguramos que la flexión queda dentro de [0,1].
                    lastHitBend = Mathf.Clamp01(lastHitBend);
                    // Si el contacto ocurre ya en el primer paso (i==0), el dedo tocaba estando abierto:
                    // dejamos el dedo en esa pose (la no-alloc ya está aplicada) y terminamos.
                    if (i == 0) {
                        EnsureNonAlloc();
                        _poseDataNonAlloc.SetFingerPose(handRotation, knuckleJoint, middleJoint, distalJoint);
                        bend = lastHitBend;
                        return true;
                    }
                    // Si no, hemos encontrado el tramo grueso donde aparece el contacto: salimos a afinarlo.
                    break;
                }
            }

            // --- SEGUNDA PASADA (FINA) ---
            // Retrocedemos un paso grueso (5/steps == 1/coarseSteps) para situarnos justo antes del contacto...
            lastHitBend -= 5f / steps;
            // ...y avanzamos en pasos pequeños (1/steps) hasta steps/10 veces, afinando dónde toca exactamente.
            for (int i = 0; i <= steps / 10f; i++) {
                // Avanzamos un incremento fino y lo mantenemos dentro de [0,1].
                lastHitBend += 1f / steps;
                lastHitBend = Mathf.Clamp01(lastHitBend);
                // Volvemos a posar y comprobar el contacto en esta flexión más precisa.
                int overlapCount = CheckFingerBlendOverlap(layermask, handRotation, tipTransform, ref fromPose, ref toPose, lastHitBend);

                // Paramos cuando la punta toca, o cuando ya hemos llegado al cierre total.
                if (overlapCount > 0 || lastHitBend >= 1) {
                    bend = lastHitBend;
                    return true;
                }
            }

            // Si nunca hubo contacto, dejamos el dedo completamente cerrado y devolvemos false.
            lastHitBend = 1f;
            toPose.SetFingerPose(handRotation, knuckleJoint, middleJoint, distalJoint);
            return false;
        }

        /// <summary>Flexiona el dedo hasta que la punta deja de solapar colliders de <paramref name="layermask"/> (inverso de <see cref="BendFingerUntilHit"/>).</summary>
        public virtual bool BendFingerUntilNoHit(int steps, int layermask, FingerPoseEnum fromPose = FingerPoseEnum.Open, FingerPoseEnum toPose = FingerPoseEnum.Closed) {
            // Igual que BendFingerUntilHit: si falta alguna de las poses pedidas, devolvemos false en vez de petar.
            if (!IsPoseSaved(fromPose) || !IsPoseSaved(toPose))
                return false;
            return BendFingerUntilNoHit(steps, layermask, ref poseData[(int)fromPose], ref poseData[(int)toPose]);
        }

        /// <summary>Flexiona de <paramref name="fromPose"/> hacia <paramref name="toPose"/> hasta que la punta deja de solapar.</summary>
        public virtual bool BendFingerUntilNoHit(int steps, int layermask, ref FingerPoseData fromPose, ref FingerPoseData toPose) {
            lastHitBend = 0;
            var handRotation = hand.transform.rotation;
            var tipTransform = tip;

            // Pasada gruesa: igual que BendFingerUntilHit pero buscamos el primer paso SIN solape.
            float coarseSteps = steps / 5f;
            for (float i = 0; i <= coarseSteps; i++) {
                lastHitBend = i / coarseSteps;
                int overlapCount = CheckFingerBlendOverlap(layermask, handRotation, tipTransform, ref fromPose, ref toPose, lastHitBend);

                // En cuanto la punta deja de tocar...
                if (overlapCount == 0) {
                    lastHitBend = Mathf.Clamp01(lastHitBend);
                    // ...si ya estaba libre en el primer paso, lo dejamos ahí.
                    if (i == 0) {
                        EnsureNonAlloc();
                        _poseDataNonAlloc.SetFingerPose(handRotation, knuckleJoint, middleJoint, distalJoint);
                        bend = lastHitBend;
                        return true;
                    }
                    // Si no, encontramos el tramo grueso donde se libera: salimos a afinar.
                    break;
                }
            }

            // Pasada fina: retrocedemos un paso grueso y avanzamos en pasos pequeños hasta quedar libre.
            lastHitBend -= 5f / steps;
            for (int i = 0; i <= steps / 10f; i++) {
                lastHitBend += 1f / steps;
                lastHitBend = Mathf.Clamp01(lastHitBend);
                int overlapCount = CheckFingerBlendOverlap(layermask, handRotation, tipTransform, ref fromPose, ref toPose, lastHitBend);

                // Paramos cuando ya no toca, o al llegar al cierre total.
                if (overlapCount == 0 || lastHitBend >= 1) {
                    bend = lastHitBend;
                    return true;
                }
            }

            lastHitBend = 1f;
            toPose.SetFingerPose(handRotation, knuckleJoint, middleJoint, distalJoint);
            return false;
        }

        /// <summary>Posa el dedo en el punto de mezcla indicado y devuelve cuántos colliders solapa su punta.</summary>
        public virtual int CheckFingerBlendOverlap(int layermask, Quaternion handRotation, Transform tipTransform, ref FingerPoseData fromPose, ref FingerPoseData toPose, float point) {
            EnsureNonAlloc();
            // 1) Interpolamos la pose del dedo entre 'from' y 'to' por el factor 'point' (0..1).
            _poseDataNonAlloc.LerpData(ref fromPose, ref toPose, point);
            // 2) Aplicamos esa pose interpolada a las articulaciones reales (mueve los huesos del dedo).
            _poseDataNonAlloc.SetFingerPose(handRotation, knuckleJoint, middleJoint, distalJoint);
            // 3) Con el dedo ya en esa posición, contamos los colliders que toca la punta (ignorando triggers).
            return Physics.OverlapSphereNonAlloc(tipTransform.position, tipRadius, results, layermask, QueryTriggerInteraction.Ignore);
        }

        //=================================================================
        //==================== CONTROL DIRECTO DE POSE ====================
        //=================================================================

        /// <summary>Fija el dedo a una flexión entre dos poses guardadas (0 = from, 1 = to), ignorando la física.</summary>
        public virtual void SetFingerBend(float bend, FingerPoseEnum fromPose = FingerPoseEnum.Open, FingerPoseEnum toPose = FingerPoseEnum.Closed) {
            // Sobrecarga cómoda con enums; delega en la versión que recibe los datos por referencia.
            SetFingerBend(bend, ref poseData[(int)fromPose], ref poseData[(int)toPose]);
        }

        /// <summary>Fija el dedo a una flexión entre dos poses (0 = from, 1 = to), ignorando la física.</summary>
        public virtual void SetFingerBend(float bend, ref FingerPoseData fromPose, ref FingerPoseData toPose) {
            // Guardamos la flexión actual.
            this.bend = bend;
            EnsureNonAlloc();
            // Interpolamos entre las dos poses y aplicamos el resultado al dedo (sin spherecast, directo).
            _poseDataNonAlloc.LerpData(ref fromPose, ref toPose, bend);
            _poseDataNonAlloc.SetFingerPose(this);
        }

        /// <summary>Actualiza la pose del dedo a un valor de flexión (alias de <see cref="SetFingerBend(float, FingerPoseEnum, FingerPoseEnum)"/>).</summary>
        public virtual void UpdateFingerPose(float bend, FingerPoseEnum fromPose = FingerPoseEnum.Open, FingerPoseEnum toPose = FingerPoseEnum.Closed) {
            SetFingerBend(bend, fromPose, toPose);
        }

        /// <summary>Pone el dedo en la pose abierta guardada.</summary>
        [ContextMenu("Pose - Abrir")]
        public virtual void ResetBend() {
            // Solo si la pose abierta existe (evita aplicar datos vacíos).
            if (IsPoseSaved(FingerPoseEnum.Open))
                poseData[(int)FingerPoseEnum.Open].SetFingerPose(this);
        }

        /// <summary>Pone el dedo en la pose cerrada guardada.</summary>
        [ContextMenu("Pose - Cerrar")]
        public virtual void Grip() {
            // Solo si la pose cerrada existe.
            if (IsPoseSaved(FingerPoseEnum.Closed))
                poseData[(int)FingerPoseEnum.Closed].SetFingerPose(this);
        }

        //=================================================================
        //===================== GUARDADO / CONSULTA =======================
        //=================================================================

        /// <summary>Guarda la forma actual del dedo en el hueco de pose indicado.</summary>
        public virtual void SavePose(Hand hand, Finger finger, FingerPoseEnum poseType) {
            // Si el array de poses no existe o no tiene el tamaño correcto (Open/Closed/PinchOpen/PinchClosed)...
            if (poseData == null || poseData.Length != (int)FingerPoseEnum.TotalPoses) {
                // ...creamos uno nuevo del tamaño correcto y copiamos lo que hubiera (para no perder poses previas).
                var old = poseData;
                poseData = new FingerPoseData[(int)FingerPoseEnum.TotalPoses];
                if (old != null)
                    for (int i = 0; i < old.Length && i < poseData.Length; i++)
                        poseData[i] = old[i];
            }

            // Si ese hueco aún no tenía datos, creamos una pose nueva; si ya los tenía, la sobrescribimos.
            if (!poseData[(int)poseType].isSet)
                poseData[(int)poseType] = new FingerPoseData(hand, finger);
            else
                poseData[(int)poseType].SetPoseData(hand, finger);
        }

        /// <summary>Copia todas las poses guardadas de otro dedo (p. ej. al reflejar una mano).</summary>
        public virtual void CopyPoseData(Finger finger) {
            // Sin datos de origen no hay nada que copiar.
            if (finger.poseData == null)
                return;
            // Ajustamos nuestro array al mismo tamaño que el de origen.
            if (poseData == null || poseData.Length != finger.poseData.Length)
                poseData = new FingerPoseData[finger.poseData.Length];

            for (int i = 0; i < finger.poseData.Length; i++) {
                // Si ya teníamos datos en este hueco, copiamos sobre ellos; si no, creamos una copia nueva.
                if (poseData[i].isSet)
                    poseData[i].CopyFromData(ref finger.poseData[i]);
                else
                    poseData[i] = new FingerPoseData(ref finger.poseData[i]);
            }
        }

        /// <summary>Devuelve si el hueco de pose indicado ya está guardado.</summary>
        public virtual bool IsPoseSaved(FingerPoseEnum poseType) {
            // Comprobamos por orden: que exista el array, que el índice sea válido, y que ese hueco tenga datos.
            return poseData != null && (int)poseType < poseData.Length && poseData[(int)poseType].isSet;
        }

        /// <summary>Devuelve la flexión alcanzada en la última llamada a <see cref="BendFingerUntilHit(int, int, FingerPoseEnum, FingerPoseEnum)"/>.</summary>
        public float GetLastHitBend() => lastHitBend;

        /// <summary>Devuelve la flexión de reposo del dedo (offset más el offset secundario en runtime).</summary>
        public virtual float GetCurrentBend() {
            // Aseguramos que el offset base está en [0,1] y le sumamos el offset secundario (de finger benders).
            bendOffset = Mathf.Clamp01(bendOffset);
            return bendOffset + secondaryOffset;
        }

        // Atajos de menú contextual para guardar cada pose desde el propio componente del dedo.
        [ContextMenu("GUARDAR - Pose Abierta")]
        void SaveOpenPose() => SavePose(hand, this, FingerPoseEnum.Open);
        [ContextMenu("GUARDAR - Pose Cerrada")]
        void SaveClosedPose() => SavePose(hand, this, FingerPoseEnum.Closed);
        [ContextMenu("GUARDAR - Pose Pinza Abierta")]
        void SavePinchOpenPose() => SavePose(hand, this, FingerPoseEnum.PinchOpen);
        [ContextMenu("GUARDAR - Pose Pinza Cerrada")]
        void SavePinchClosedPose() => SavePose(hand, this, FingerPoseEnum.PinchClosed);

        // Dibuja la esfera de la punta en la escena al seleccionar el dedo, para poder ajustar el tipRadius a ojo.
        void OnDrawGizmosSelected() {
            if (tip == null)
                return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(tip.position, tipRadius);
        }
    }
}
