using UnityEngine;

namespace ProceduralHands {
    /// <summary>
    /// Utilidades estáticas de matemáticas/transforms que comparte el sistema de poses. Incluye la
    /// extracción rápida de traslación/rotación/escala de una <see cref="Matrix4x4"/> y dos transforms
    /// ocultos reutilizables ("reglas") para cálculos espaciales sin reservar memoria (p. ej. poses avanzadas).
    /// </summary>
    public static class HandUtils {

        /// <summary>Devuelve la componente de traslación de una matriz TRS.</summary>
        public static Vector3 ExtractPosition(ref Matrix4x4 matrix) {
            // En una matriz TRS, la posición está en la 4ª columna (m03, m13, m23).
            return new Vector3(matrix.m03, matrix.m13, matrix.m23);
        }

        /// <summary>Devuelve la componente de rotación de una matriz TRS (forward = columna 2, up = columna 1).</summary>
        public static Quaternion ExtractRotation(ref Matrix4x4 matrix) {
            // El eje "forward" (Z) es la 3ª columna y el "up" (Y) la 2ª columna de la matriz.
            Vector3 forward = new Vector3(matrix.m02, matrix.m12, matrix.m22);
            Vector3 up = new Vector3(matrix.m01, matrix.m11, matrix.m21);
            // Si la matriz está degenerada (columnas casi nulas), LookRotation fallaría: devolvemos identidad.
            if (forward.sqrMagnitude < 1e-12f || up.sqrMagnitude < 1e-12f)
                return Quaternion.identity;
            // Reconstruimos la rotación a partir de los ejes forward/up.
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>Devuelve la componente de escala de una matriz TRS (longitud de cada columna base).</summary>
        public static Vector3 ExtractScale(ref Matrix4x4 matrix) {
            // La escala en cada eje es la longitud (magnitud) de la columna correspondiente de la matriz.
            return new Vector3(
                new Vector3(matrix.m00, matrix.m10, matrix.m20).magnitude,  // escala X = |columna 0|
                new Vector3(matrix.m01, matrix.m11, matrix.m21).magnitude,  // escala Y = |columna 1|
                new Vector3(matrix.m02, matrix.m12, matrix.m22).magnitude); // escala Z = |columna 2|
        }

        // Transforms ocultos reutilizables para cálculos temporales (no se guardan en la escena).
        static Transform _ruler;
        static Transform _rulerChild;

        /// <summary>Transform oculto y no guardado, reutilizado para cálculos temporales en espacio de mundo.</summary>
        public static Transform transformRuler {
            get {
                // Lo creamos una sola vez (perezosamente) y lo marcamos para que no aparezca ni se guarde.
                if (_ruler == null) {
                    var go = new GameObject("PH_TransformRuler") { hideFlags = HideFlags.HideAndDontSave };
                    _ruler = go.transform;
                }
                return _ruler;
            }
        }

        /// <summary>Hijo oculto de <see cref="transformRuler"/>, útil para leer valores tras mover el padre.</summary>
        public static Transform transformRulerChild {
            get {
                if (_rulerChild == null) {
                    var go = new GameObject("PH_TransformRulerChild") { hideFlags = HideFlags.HideAndDontSave };
                    _rulerChild = go.transform;
                    // Lo colgamos del "ruler" padre: así, al rotar/mover el padre, este hijo se mueve con él.
                    _rulerChild.SetParent(transformRuler, false);
                }
                return _rulerChild;
            }
        }

        /// <summary>
        /// Resuelve el <see cref="Grabbable"/> asociado a un GameObject, ya sea directamente o a través
        /// de un enlace <see cref="GrabbableChild"/> añadido a los colliders hijos.
        /// </summary>
        public static bool HasGrabbable(this GameObject obj, out Grabbable grabbable) {
            // Caso 1: el propio GameObject tiene el componente Grabbable.
            if (obj.TryGetComponent(out grabbable))
                return true;
            // Caso 2: es un collider hijo con un enlace GrabbableChild que apunta al grabbable padre.
            if (obj.TryGetComponent(out GrabbableChild child) && child.grabParent != null) {
                grabbable = child.grabParent;
                return true;
            }
            // No es agarrable.
            grabbable = null;
            return false;
        }
    }
}
