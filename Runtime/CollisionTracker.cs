using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralHands {
    /// <summary>Se dispara con el otro GameObject cuando una colisión/trigger rastreado empieza o termina.</summary>
    public delegate void CollisionEvent(GameObject from);

    /// <summary>
    /// Rastrea con qué GameObjects está en contacto este cuerpo (colisiones sólidas y/o triggers).
    /// Acumula los contactos de <c>OnCollisionStay</c>/<c>OnTriggerStay</c> y los compara una vez por
    /// paso de física, en un <c>FixedUpdate</c> tardío. Dispara eventos de primer-entra / último-sale,
    /// de modo que los consumidores (la mano, los grabbables, las pose areas) reciben notificaciones
    /// limpias de entrada y salida.
    /// </summary>
    public class CollisionTracker : MonoBehaviour {

        [Label("Desactivar rastreo de colisiones", "Si está activo, no se rastrean las colisiones sólidas.")]
        public bool disableCollisionTracking = false;
        [Label("Desactivar rastreo de triggers", "Si está activo, no se rastrean los triggers.")]
        public bool disableTriggersTracking = false;

        // Eventos públicos a los que se suscriben la mano y los grabbables.
        public event CollisionEvent OnCollisionFirstEnter; // primer objeto sólido en entrar en contacto
        public event CollisionEvent OnCollisionLastExit;   // último contacto sólido en salir
        public event CollisionEvent OnTriggerFirstEnter;   // primer trigger en entrar
        public event CollisionEvent OnTriggerLastExit;     // último trigger en salir

        public int collisionCount => collisionObjects.Count;
        public int triggerCount => triggerObjects.Count;

        // Capacidad inicial de las listas (evita realocaciones en escenas con muchos contactos).
        const int MaxTracked = 256;

        // Contactos confirmados en el frame actual.
        public List<GameObject> triggerObjects { get; protected set; } = new List<GameObject>(MaxTracked);
        public List<GameObject> collisionObjects { get; protected set; } = new List<GameObject>(MaxTracked);
        // Contactos que se están acumulando durante este paso de física (se vuelcan a los de arriba al comparar).
        protected List<GameObject> nextTriggerObjects { get; set; } = new List<GameObject>(MaxTracked);
        protected List<GameObject> nextCollisionObjects { get; set; } = new List<GameObject>(MaxTracked);

        Coroutine lateFixedUpdate;
        // Espera reutilizable para no reservar memoria cada iteración de la corrutina.
        readonly WaitForFixedUpdate waitForFixed = new WaitForFixedUpdate();

        /// <summary>Vacía todas las listas de contactos.</summary>
        public void CleanUp() {
            triggerObjects.Clear();
            nextTriggerObjects.Clear();
            collisionObjects.Clear();
            nextCollisionObjects.Clear();
        }

        protected virtual void OnEnable() {
            // Arrancamos la corrutina que procesa los contactos al final de cada paso de física.
            lateFixedUpdate = StartCoroutine(LateFixedUpdate());
        }

        protected virtual void OnDisable() {
            // Al desactivar, avisamos de que todo lo que tocábamos "sale" (para no dejar estados colgados).
            for (int i = 0; i < collisionObjects.Count; i++)
                if (collisionObjects[i] != null)
                    OnCollisionLastExit?.Invoke(collisionObjects[i]);
            for (int i = 0; i < triggerObjects.Count; i++)
                if (triggerObjects[i] != null)
                    OnTriggerLastExit?.Invoke(triggerObjects[i]);

            CleanUp();
            if (lateFixedUpdate != null)
                StopCoroutine(lateFixedUpdate);
        }

        IEnumerator LateFixedUpdate() {
            // Se ejecuta después del paso de física para que los eventos entra/sale coincidan con la
            // actualización de colisiones de ese mismo paso.
            while (true) {
                yield return waitForFixed;
                CheckTrackedObjects();
            }
        }

        // Compara los contactos "next" (acumulados este paso) con los del frame anterior y dispara los eventos.
        void CheckTrackedObjects() {
            if (!disableCollisionTracking) {
                // 1) Los que estaban en contacto y ya NO aparecen en 'next' (o se desactivaron) → "último sale".
                for (int i = 0; i < collisionObjects.Count; i++) {
                    if (collisionObjects[i] == null || !collisionObjects[i].activeInHierarchy || !nextCollisionObjects.Contains(collisionObjects[i]))
                        OnCollisionLastExit?.Invoke(collisionObjects[i]);
                }
                // 2) Los nuevos en 'next' que no estaban antes → "primer entra" (limpiando nulos/inactivos).
                for (int i = nextCollisionObjects.Count - 1; i >= 0; i--) {
                    if (nextCollisionObjects[i] == null || !nextCollisionObjects[i].activeInHierarchy)
                        nextCollisionObjects.RemoveAt(i);
                    else if (!collisionObjects.Contains(nextCollisionObjects[i]))
                        OnCollisionFirstEnter?.Invoke(nextCollisionObjects[i]);
                }
                // 3) 'next' pasa a ser el contacto actual y se vacía para el siguiente paso.
                collisionObjects.Clear();
                collisionObjects.AddRange(nextCollisionObjects);
                nextCollisionObjects.Clear();
            }

            // Lo mismo para los triggers.
            if (!disableTriggersTracking) {
                for (int i = 0; i < triggerObjects.Count; i++) {
                    if (triggerObjects[i] == null || !triggerObjects[i].activeInHierarchy || !nextTriggerObjects.Contains(triggerObjects[i]))
                        OnTriggerLastExit?.Invoke(triggerObjects[i]);
                }
                for (int i = nextTriggerObjects.Count - 1; i >= 0; i--) {
                    if (nextTriggerObjects[i] == null || !nextTriggerObjects[i].activeInHierarchy)
                        nextTriggerObjects.RemoveAt(i);
                    else if (!triggerObjects.Contains(nextTriggerObjects[i]))
                        OnTriggerFirstEnter?.Invoke(nextTriggerObjects[i]);
                }
                triggerObjects.Clear();
                triggerObjects.AddRange(nextTriggerObjects);
                nextTriggerObjects.Clear();
            }
        }

        // Unity llama a esto mientras dura una colisión sólida: acumulamos el objeto si no estaba ya.
        protected virtual void OnCollisionStay(Collision collision) {
            if (!disableCollisionTracking && !nextCollisionObjects.Contains(collision.collider.gameObject))
                nextCollisionObjects.Add(collision.collider.gameObject);
        }

        // Igual para los triggers.
        protected virtual void OnTriggerStay(Collider other) {
            if (!disableTriggersTracking && !nextTriggerObjects.Contains(other.gameObject))
                nextTriggerObjects.Add(other.gameObject);
        }
    }
}
