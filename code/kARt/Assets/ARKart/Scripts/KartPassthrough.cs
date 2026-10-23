using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;

namespace ARKart
{
    /// <summary>
    /// Passthrough acceso / spento (tasto X, o V da tastiera nell'editor).
    ///   acceso: si vede la stanza reale (ARCameraManager attivo, sfondo trasparente)
    ///   spento: la stanza è coperta da un ambiente virtuale (skybox + pavimento a griglia),
    ///           per guidare immersi solo nel video FPV
    /// La scelta viene ricordata tra un avvio e l'altro.
    /// </summary>
    public class KartPassthrough : MonoBehaviour
    {
        [Header("Riferimenti (impostati dal menu kARt, altrimenti cercati all'avvio)")]
        public ARCameraManager cameraManager;
        public Camera xrCamera;
        public XROrigin xrOrigin;
        [Tooltip("Oggetto con pavimento e decorazioni, attivo solo a passthrough spento")]
        public GameObject virtualEnvironment;
        public Material skybox;

        [Tooltip("Altezza degli occhi usata se il sistema non fornisce il pavimento (origine Device)")]
        public float eyeHeight = 1.6f;

        const string PrefKey = "kart_passthrough";

        public bool PassthroughOn { get; private set; } = true;

        InputAction toggleAction;

        void OnEnable()
        {
            if (xrCamera == null) xrCamera = Camera.main;
            if (cameraManager == null && xrCamera != null) cameraManager = xrCamera.GetComponent<ARCameraManager>();
            if (xrOrigin == null) xrOrigin = FindFirstObjectByType<XROrigin>();

            toggleAction = new InputAction("Passthrough", InputActionType.Button, "<XRController>{LeftHand}/{PrimaryButton}");
            toggleAction.AddBinding("<Keyboard>/v");
            toggleAction.Enable();

            Apply(PlayerPrefs.GetInt(PrefKey, 1) == 1);
        }

        void OnDisable()
        {
            toggleAction?.Dispose();
        }

        void Update()
        {
            if (toggleAction.WasPressedThisFrame())
            {
                Apply(!PassthroughOn);
                PlayerPrefs.SetInt(PrefKey, PassthroughOn ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        public void Apply(bool on)
        {
            PassthroughOn = on;
            if (cameraManager != null) cameraManager.enabled = on;
            if (xrCamera != null)
            {
                // con il passthrough lo sfondo deve essere trasparente, altrimenti copre la stanza
                xrCamera.clearFlags = on || skybox == null ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
                xrCamera.backgroundColor = on ? Color.clear : new Color(0.07f, 0.08f, 0.09f, 1f);
            }
            if (!on && skybox != null) RenderSettings.skybox = skybox;
            if (virtualEnvironment != null)
            {
                if (!on) PlaceFloor();
                virtualEnvironment.SetActive(!on);
            }
        }

        // Il pavimento virtuale va all'altezza di quello reale
        void PlaceFloor()
        {
            if (xrOrigin == null || xrCamera == null) return;
            float floorY = xrOrigin.CurrentTrackingOriginMode == UnityEngine.XR.TrackingOriginModeFlags.Floor
                ? xrOrigin.Origin.transform.position.y
                : xrCamera.transform.position.y - eyeHeight;
            var p = virtualEnvironment.transform.position;
            virtualEnvironment.transform.position = new Vector3(p.x, floorY, p.z);
        }
    }
}
