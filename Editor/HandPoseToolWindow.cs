using UnityEditor;
using UnityEngine;

namespace ProceduralHands.EditorTools {
    /// <summary>
    /// Ventana flotante para autorar una pose de agarre sobre una copia de mano generada: sliders de
    /// flexión por dedo, una flexión global, una Auto Pose (flexionar hasta tocar), espejado
    /// Invertir X/Y/Z, opciones avanzadas de ángulo/rango para <see cref="GrabbablePoseAdvanced"/>, y
    /// Guardar / Borrar.
    /// </summary>
    public class HandPoseToolWindow : EditorWindow {

        // Mano-copia que estamos posando y contenedor de pose donde se guardará el resultado.
        Hand handCopy;
        HandPoseDataContainer pose;

        // Estado de la UI por dedo: máscara de selección, flexión actual y última flexión aplicada.
        bool[] fingerMask;
        float[] fingerBend;
        float[] lastFingerBend;
        float globalBend; // flexión global a aplicar a los dedos seleccionados

        enum Axis { X, Y, Z }

        /// <summary>Abre la herramienta para la copia de mano y la pose dadas.</summary>
        public static void ShowWindow(Hand hand, HandPoseDataContainer pose) {
            var window = GetWindow<HandPoseToolWindow>(true, "Hand Pose Tool");
            window.handCopy = hand;
            window.pose = pose;
            window.minSize = new Vector2(250, 360);
            window.Init();
            window.Show();
        }

        // Inicializa los arrays de estado por dedo (todos seleccionados por defecto).
        void Init() {
            if (handCopy == null || handCopy.fingers == null)
                return;
            int n = handCopy.fingers.Length;
            fingerMask = new bool[n];
            fingerBend = new float[n];
            lastFingerBend = new float[n];
            for (int i = 0; i < n; i++)
                fingerMask[i] = true;
        }

        void OnGUI() {
            // Si la copia desapareció (p. ej. se borró en la escena), cerramos la ventana.
            if (handCopy == null || handCopy.fingers == null) {
                Close();
                return;
            }
            // Si cambió el número de dedos, reinicializamos los arrays de estado.
            if (fingerMask == null || fingerMask.Length != handCopy.fingers.Length)
                Init();

            EditorGUILayout.LabelField("Hand Pose Tool", EditorStyles.boldLabel);
            // Cabecera con el nombre de la pose y el lado de la mano.
            EditorGUILayout.LabelField(pose != null ? $"Pose: {pose.name}  ({(handCopy.left ? "Izquierda" : "Derecha")})" : "");
            EditorGUILayout.Space();

            // --- Sliders de flexión por dedo ---
            for (int i = 0; i < handCopy.fingers.Length; i++) {
                var finger = handCopy.fingers[i];
                if (finger == null)
                    continue;

                // Casilla de selección (qué dedos afectan las acciones globales) + slider de flexión 0..1.
                EditorGUILayout.BeginHorizontal();
                fingerMask[i] = EditorGUILayout.ToggleLeft(finger.name, fingerMask[i], GUILayout.Width(120));
                fingerBend[i] = EditorGUILayout.Slider(fingerBend[i], 0f, 1f);
                EditorGUILayout.EndHorizontal();

                // Si el dedo está seleccionado, su slider cambió y se puede flexionar, aplicamos la nueva flexión.
                if (fingerMask[i] && !Mathf.Approximately(fingerBend[i], lastFingerBend[i]) && CanBend(finger)) {
                    Undo.RegisterFullObjectHierarchyUndo(handCopy.gameObject, "Pose Finger");
                    finger.SetFingerBend(fingerBend[i]);
                    // Recordamos el valor aplicado para no reaplicar en cada repintado de la GUI.
                    lastFingerBend[i] = fingerBend[i];
                }
            }

            // --- Flexión global ---
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            globalBend = EditorGUILayout.Slider(globalBend, 0f, 1f);
            // Aplica la flexión global a todos los dedos seleccionados que puedan flexionar.
            if (GUILayout.Button("Aplicar flexión a los dedos")) {
                Undo.RegisterFullObjectHierarchyUndo(handCopy.gameObject, "Pose Fingers");
                for (int i = 0; i < handCopy.fingers.Length; i++) {
                    if (fingerMask[i] && CanBend(handCopy.fingers[i])) {
                        handCopy.fingers[i].SetFingerBend(globalBend);
                        // Sincronizamos los sliders individuales con el valor global aplicado.
                        fingerBend[i] = globalBend;
                        lastFingerBend[i] = globalBend;
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            // --- Auto Pose: flexiona cada dedo seleccionado hasta tocar algo ---
            if (GUILayout.Button("Auto Pose (flexionar hasta tocar)")) {
                Undo.RegisterFullObjectHierarchyUndo(handCopy.gameObject, "Auto Pose");
                // Máscara que EXCLUYE las capas de la mano (no queremos que un dedo "choque" con la propia mano).
                int mask = ~HandBase.GetHandsLayerMask();
                for (int i = 0; i < handCopy.fingers.Length; i++)
                    if (fingerMask[i] && CanBend(handCopy.fingers[i]))
                        handCopy.fingers[i].BendFingerUntilHit(100, mask);
            }

            // --- Espejado ---
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Espejar", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Invertir X")) Invert(Axis.X);
            if (GUILayout.Button("Invertir Y")) Invert(Axis.Y);
            if (GUILayout.Button("Invertir Z")) Invert(Axis.Z);
            EditorGUILayout.EndHorizontal();

            // Opciones avanzadas solo si la pose es del tipo avanzado (rotación/deslizamiento por eje).
            if (pose is GrabbablePoseAdvanced advanced)
                DrawAdvanced(advanced);

            // --- Guardar pose ---
            EditorGUILayout.Space();
            if (pose != null) {
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = Color.green;
                if (GUILayout.Button(handCopy.left ? "Guardar pose izquierda" : "Guardar pose derecha")) {
                    // Sincronizamos el índice de pose con el de la mano antes de guardar.
                    if (pose.poseIndex != handCopy.poseIndex)
                        pose.poseIndex = handCopy.poseIndex;
                    pose.EditorSaveGrabPose(handCopy);
                }
                GUI.backgroundColor = prev;
            }

            // --- Borrar la copia de mano (limpia la escena) ---
            EditorGUILayout.Space();
            Color prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("Borrar copia de mano")) {
                // Destruimos el contenedor (padre) si existe; en su defecto, la propia copia.
                var go = handCopy.transform.parent != null ? handCopy.transform.parent.gameObject : handCopy.gameObject;
                if (pose != null)
                    Selection.activeGameObject = pose.gameObject;
                Undo.DestroyObjectImmediate(go);
                Close();
                return;
            }
            GUI.backgroundColor = prevColor;

            // Marcamos la copia sucia para que se refresque en inspector/escena.
            if (handCopy != null)
                EditorUtility.SetDirty(handCopy);
        }

        // Un dedo solo puede flexionarse si tiene guardadas las poses abierta y cerrada (para interpolar entre ellas).
        bool CanBend(Finger finger) {
            return finger != null && finger.IsPoseSaved(FingerPoseEnum.Open) && finger.IsPoseSaved(FingerPoseEnum.Closed);
        }

        // Espeja la mano en el eje dado escalando el contenedor padre (y rotándolo según el eje).
        void Invert(Axis axis) {
            var parent = handCopy.transform.parent;
            if (parent == null)
                return;
            Undo.RecordObject(parent, "Invert Hand");
            Undo.RecordObject(handCopy, "Invert Hand");

            // El espejo SIEMPRE invierte la escala X; para Y/Z además rotamos el padre 180° en el eje correspondiente.
            var scale = parent.localScale;
            switch (axis) {
                case Axis.X:
                    scale.x = -scale.x;
                    break;
                case Axis.Y:
                    scale.x = -scale.x;
                    parent.Rotate(0, 0, 180);
                    break;
                case Axis.Z:
                    scale.x = -scale.x;
                    parent.Rotate(0, 180, 0);
                    break;
            }
            parent.localScale = scale;
            // Al espejar, la mano cambia de lado.
            handCopy.left = !handCopy.left;
        }

        // Dibuja los campos avanzados (ángulo/rango) y previsualiza el resultado en la mano cuando cambian.
        void DrawAdvanced(GrabbablePoseAdvanced advanced) {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Opciones de pose avanzada", EditorStyles.boldLabel);

            // Detectamos cambios para aplicar la previsualización solo cuando algún valor cambie.
            EditorGUI.BeginChangeCheck();
            advanced.minAngle = EditorGUILayout.IntField("Ángulo mínimo", advanced.minAngle);
            advanced.maxAngle = EditorGUILayout.IntField("Ángulo máximo", advanced.maxAngle);
            advanced.testAngle = EditorGUILayout.IntSlider("Ángulo de prueba", advanced.testAngle, advanced.minAngle, advanced.maxAngle);
            advanced.minRange = EditorGUILayout.FloatField("Rango mínimo", advanced.minRange);
            advanced.maxRange = EditorGUILayout.FloatField("Rango máximo", advanced.maxRange);
            advanced.testRange = EditorGUILayout.Slider("Rango de prueba", advanced.testRange, advanced.minRange, advanced.maxRange);
            if (EditorGUI.EndChangeCheck()) {
                // Aplicamos los valores de prueba a la mano del editor para ver el resultado, y marcamos sucio.
                advanced.EditorTestValues(handCopy);
                EditorUtility.SetDirty(advanced);
            }
        }
    }
}
