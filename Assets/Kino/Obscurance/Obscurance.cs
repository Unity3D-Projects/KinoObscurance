﻿//
// Kino/Obscurance - SSAO (screen-space ambient obscurance) effect for Unity
//
// Copyright (C) 2016 Keijiro Takahashi
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
using UnityEngine;
using UnityEngine.Rendering;

namespace Kino
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Kino Image Effects/Obscurance")]
    public partial class Obscurance : MonoBehaviour
    {
        #region Public Properties

        /// Obscurance intensity
        public float intensity {
            get { return _intensity; }
            set { _intensity = value; }
        }

        [SerializeField, Range(0, 4)]
        float _intensity = 1;

        /// Sampling radius
        public float radius {
            get { return Mathf.Max(_radius, 1e-5f); }
            set { _radius = value; }
        }

        [SerializeField]
        float _radius = 0.3f;

        /// Obscurance estimator type
        public EstimatorType estimatorType {
            get { return _estimatorType; }
            set { _estimatorType = value; }
        }

        public enum EstimatorType {
            AngleBased,
            DistanceBased
        }

        [SerializeField]
        EstimatorType _estimatorType = EstimatorType.DistanceBased;

        /// Sample count options
        public SampleCount sampleCount {
            get { return _sampleCount; }
            set { _sampleCount = value; }
        }

        public enum SampleCount { Low, Medium, Variable }

        [SerializeField]
        SampleCount _sampleCount = SampleCount.Medium;

        /// Variable sample count value
        public int sampleCountValue {
            get { return Mathf.Clamp(_sampleCountValue, 1, 120); }
            set { _sampleCountValue = value; }
        }

        [SerializeField]
        int _sampleCountValue = 20;

        /// Noise filter
        public int noiseFilter {
            get { return _noiseFilter; }
            set { _noiseFilter = value; }
        }

        [SerializeField, Range(0, 2)]
        int _noiseFilter = 2;

        /// Downsampling (half-resolution mode)
        public bool downsampling {
            get { return _downsampling; }
            set { _downsampling = value; }
        }

        [SerializeField]
        bool _downsampling = false;

        /// Only affects ambient lighting
        public bool ambientOnly {
            get {
                // Only enabled with the deferred shading rendering path
                // and HDR rendering.
                if (_ambientOnly && targetCamera.hdr)
                {
                    var path = targetCamera.actualRenderingPath;
                    return path == RenderingPath.DeferredShading;
                }
                return false;
            }
            set { _ambientOnly = value; }
        }

        [SerializeField]
        bool _ambientOnly = false;

        #endregion

        #region Private Properties

        // Quad mesh for blitting (reference to build-in asset)
        [SerializeField] Mesh _quadMesh;

        // AO shader material
        Material aoMaterial {
            get {
                if (_aoMaterial == null) {
                    var shader = Shader.Find("Hidden/Kino/Obscurance");
                    _aoMaterial = new Material(shader);
                    _aoMaterial.hideFlags = HideFlags.DontSave;
                }
                return _aoMaterial;
            }
        }

        [SerializeField] Shader _aoShader;
        Material _aoMaterial;

        // Command buffer for the AO pass
        CommandBuffer aoCommands {
            get {
                if (_aoCommands == null) {
                    _aoCommands = new CommandBuffer();
                    _aoCommands.name = "Kino.Obscurance";
                }
                return _aoCommands;
            }
        }

        CommandBuffer _aoCommands;

        // Target camera
        Camera targetCamera {
            get { return GetComponent<Camera>(); }
        }

        // Property observer
        PropertyObserver propertyObserver { get; set; }

        #endregion

        #region Effect Pass

        // Build the AO pass commands (used in the ambient-only mode).
        void BuildAOCommands()
        {
            var cb = aoCommands;

            var tw = targetCamera.pixelWidth;
            var th = targetCamera.pixelHeight;
            var format = RenderTextureFormat.R8;

            if (downsampling) {
                tw /= 2;
                th /= 2;
            }

            // AO buffer
            var m = aoMaterial;
            var rtMask = Shader.PropertyToID("_ObscuranceTexture");
            cb.GetTemporaryRT(
                rtMask, tw, th, 0, FilterMode.Bilinear, format
            );

            // AO estimation
            cb.Blit(null, rtMask, m, 0);

            if (noiseFilter > 0)
            {
                // Blur buffer
                var rtBlur = Shader.PropertyToID("_ObscuranceBlurTexture");
                cb.GetTemporaryRT(
                    rtBlur, tw, th, 0, FilterMode.Bilinear, format
                );

                // Geometry-aware blur
                for (var i = 0; i < noiseFilter; i++)
                {
                    cb.SetGlobalVector("_BlurVector", Vector2.right);
                    cb.Blit(rtMask, rtBlur, m, 1);

                    cb.SetGlobalVector("_BlurVector", Vector2.up);
                    cb.Blit(rtBlur, rtMask, m, 1);
                }

                cb.ReleaseTemporaryRT(rtBlur);
            }

            // Combine AO to the G-buffer.
            var mrt = new RenderTargetIdentifier[] {
                BuiltinRenderTextureType.GBuffer0,      // Albedo, Occ
                BuiltinRenderTextureType.CameraTarget   // Ambient
            };
            cb.SetRenderTarget(mrt, BuiltinRenderTextureType.CameraTarget);
            cb.DrawMesh(_quadMesh, Matrix4x4.identity, m, 0, 3);

            cb.ReleaseTemporaryRT(rtMask);
        }

        // Execute the AO pass immediately (used in the forward mode).
        void ExecuteAOPass(RenderTexture source, RenderTexture destination)
        {
            var tw = source.width;
            var th = source.height;

            if (downsampling) {
                tw /= 2;
                th /= 2;
            }

            // AO buffer
            var m = aoMaterial;
            var rtMask = RenderTexture.GetTemporary(
                tw, th, 0, RenderTextureFormat.R8
            );

            // AO estimation
            Graphics.Blit(null, rtMask, m, 0);

            if (noiseFilter > 0)
            {
                // Blur buffer
                var rtBlur = RenderTexture.GetTemporary(
                    tw, th, 0, RenderTextureFormat.R8
                );

                // Geometry-aware blur
                for (var i = 0; i < noiseFilter; i++)
                {
                    m.SetVector("_BlurVector", Vector2.right);
                    Graphics.Blit(rtMask, rtBlur, m, 1);

                    m.SetVector("_BlurVector", Vector2.up);
                    Graphics.Blit(rtBlur, rtMask, m, 1);
                }

                RenderTexture.ReleaseTemporary(rtBlur);
            }

            // Combine AO with the source.
            m.SetTexture("_ObscuranceTexture", rtMask);
            Graphics.Blit(source, destination, m, 2);

            RenderTexture.ReleaseTemporary(rtMask);
        }

        // Update the common material properties.
        void UpdateMaterialProperties()
        {
            var m = aoMaterial;
            m.shaderKeywords = null;

            m.SetFloat("_Intensity", intensity);
            m.SetFloat("_Contrast", 0.6f);
            m.SetFloat("_Radius", radius);
            m.SetFloat("_DepthFallOff", 100);
            m.SetFloat("_TargetScale", downsampling ? 0.5f : 1);

            // Render target (color buffer or G-buffer)
            if (ambientOnly)
                m.EnableKeyword("_TARGET_GBUFFER");

            // AO method (angle based or distance based)
            if (estimatorType == EstimatorType.DistanceBased)
                m.EnableKeyword("_METHOD_DISTANCE");

            // Sample count
            if (sampleCount == SampleCount.Low)
                m.EnableKeyword("_COUNT_LOW");
            else if (sampleCount == SampleCount.Medium)
                m.EnableKeyword("_COUNT_MEDIUM");
            else
                m.SetInt("_SampleCount", sampleCountValue);
        }

        #endregion

        #region MonoBehaviour Functions

        void OnEnable()
        {
            if (ambientOnly)
            {
                // Register the command buffer for the ambient only mode.
                targetCamera.AddCommandBuffer(
                    CameraEvent.BeforeReflections, aoCommands
                );
            }
            else
            {
                // Needs CameraDepthNormals texture for the forward mode.
                targetCamera.depthTextureMode = DepthTextureMode.DepthNormals;
            }
        }

        void OnDisable()
        {
            // Destroy all the temporary resources.
            if (_aoMaterial != null) DestroyImmediate(_aoMaterial);

            _aoMaterial = null;

            if (_aoCommands != null) targetCamera.RemoveCommandBuffer(
                CameraEvent.BeforeReflections, _aoCommands
            );

            _aoCommands = null;
        }

        void Update()
        {
            if (propertyObserver.CheckNeedsReset(this, targetCamera))
            {
                // Reinitialize all the resources. Not efficient but just works.
                OnDisable();
                OnEnable();

                if (ambientOnly)
                {
                    // Build the command buffer for the ambient only mode.
                    aoCommands.Clear();
                    BuildAOCommands();
                }

                propertyObserver.Update(this, targetCamera);
            }

            // Update the material properties (later used in the AO commands).
            if (ambientOnly) UpdateMaterialProperties();
        }

        [ImageEffectOpaque]
        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (ambientOnly)
            {
                // Do nothing in the ambient only mode.
                Graphics.Blit(source, destination);
            }
            else
            {
                // Execute the AO pass.
                UpdateMaterialProperties();
                ExecuteAOPass(source, destination);
            }
        }

        #endregion
    }
}
