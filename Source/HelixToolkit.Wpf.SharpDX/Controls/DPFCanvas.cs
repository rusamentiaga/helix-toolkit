﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="DPFCanvas.cs" company="Helix Toolkit">
//   Copyright (c) 2014 Helix Toolkit contributors
// </copyright>
// <summary>
//
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace HelixToolkit.Wpf.SharpDX
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Threading;

    using global::SharpDX;

    using global::SharpDX.Direct3D11;

    using global::SharpDX.DXGI;

    using Utilities;
    using Extensions;

    using Device = global::SharpDX.Direct3D11.Device;
    using Core2D;
    using Model;
    using Render;
    using System.Threading.Tasks;

    // ---- BASED ON ORIGNAL CODE FROM -----
    // Copyright (c) 2010-2012 SharpDX - Alexandre Mutel
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

    public class DPFCanvas : Image, IRenderHost
    {
        private IDX11RenderBufferProxy buffers;
        private readonly Stopwatch renderTimer;
        private Device device;

        private DX11ImageSource surfaceD3D;
        private IViewport3DX viewport;
        private RenderContext renderContext;
        private bool sceneAttached;
        private bool pendingValidationCycles = true;
        private TimeSpan lastRenderingDuration;
        private bool loaded = false;
        public bool IsRendering { set; get; } = true;

        public RenderTargetView ColorBufferView { get { return buffers.ColorBufferView; } }
        public DepthStencilView DepthStencilBufferView { get { return buffers.DepthStencilBufferView; } }
        /// <summary>
        /// Get RenderContext
        /// </summary>
        public IRenderContext RenderContext { get { return renderContext; } }

        private IRenderer renderer;

        private RenderParameter renderParameter;

#if MSAA
        private Texture2D renderTargetNMS;
#endif

        /// <summary>
        /// Fired whenever an exception occurred on this object.
        /// </summary>
        public event EventHandler<RelayExceptionEventArgs> ExceptionOccurred = delegate { };

        /// <summary>
        /// 
        /// </summary>
        public Color4 ClearColor
        {
            set;get;
        }

        /// <summary>
        /// 
        /// </summary>
        public bool IsShadowMapEnabled { get; private set; }

#if MSAA
        private MSAALevel msaa = MSAALevel.Disable;
        /// <summary>
        /// Set MSAA level. If set to Two/Four/Eight, the actual level is set to minimum between Maximum and Two/Four/Eight
        /// </summary>
        public MSAALevel MSAA
        {
            get { return msaa; }
            set
            {
                if (msaa != value)
                {
                    msaa = value;
                    if (renderTimer.IsRunning)
                    {
                        StopRendering();
                        CreateAndBindTargets();
                        SetDefaultRenderTargets();
                        StartRendering();
                        InvalidateRender();
                    }
                }
            }
        }
#endif
        /// <summary>
        /// Gets or sets the maximum time that rendering is allowed to take. When exceeded,
        /// the next cycle will be enqueued at <see cref="DispatcherPriority.Input"/> to reduce input lag.
        /// </summary>
        public TimeSpan MaxRenderingDuration { get; set; }

        private uint mMaxFPS = 60;
        public uint MaxFPS
        {
            set
            {
                mMaxFPS = value;
                skipper.Threshold = (long)Math.Floor(1000.0 / mMaxFPS);
            }
            get
            {
                return mMaxFPS;
            }
        }
        /// <summary>
        /// 
        /// </summary>
        public IRenderTechnique RenderTechnique
        {
            get { return renderTechnique; }
            private set
            {
                renderTechnique = value;
            }
        }
        private IRenderTechnique renderTechnique;

        public bool IsDeferredLighting { private set; get; } = false;

        private bool enableRenderFrustum = false;
        public bool EnableRenderFrustum
        {
            set
            {
                enableRenderFrustum = value;
                if (renderContext != null)
                {
                    renderContext.EnableBoundingFrustum = value;
                }
            }
            get
            {
                return enableRenderFrustum;
            }
        }
        /// <summary>
        /// The instance of currently attached IRenderable - this is in general the Viewport3DX
        /// </summary>
        public IViewport3DX Viewport
        {
            get { return viewport; }
            set
            {
                if (ReferenceEquals(viewport, value))
                {
                    return;
                }

                DetachRenderables();
                viewport = value;
                InvalidateRender();
            }
        }

        /// <summary>
        /// The currently used Direct3D Device
        /// </summary>
        Device IRenderHost.Device
        {
            get { return device; }
        }
        /// <summary>
        /// 
        /// </summary>
        public bool EnableSharingModelMode { set; get; } = false;

        /// <summary>
        /// 
        /// </summary>
        public IModelContainer SharedModelContainer { set; get; } = null;

        private IEffectsManager effectsManager;
        public IEffectsManager EffectsManager
        {
            set
            {
                if (effectsManager != value)
                {
                    effectsManager = value;
                    RestartRendering();
                }
            }
            get
            {
                return effectsManager;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the control is in design mode
        /// (running in Blend or Visual Studio).
        /// </summary>
        public static bool IsInDesignMode
        {
            get
            {
                var prop = DesignerProperties.IsInDesignModeProperty;
                return (bool)DependencyPropertyDescriptor.FromProperty(prop, typeof(FrameworkElement)).Metadata.DefaultValue;
            }
        }

        /// <summary>
        /// Indicates if DPFCanvas busy on rendering.
        /// </summary>
        public bool IsBusy { get { return pendingValidationCycles; } }

        public ID2DTarget D2DControls { get; } = new D2DControlWrapper();

        /// <summary>
        /// 
        /// </summary>
        //private DispatcherOperation pendingInvalidateOperation = null;
        /// <summary>
        /// 
        /// </summary>
        //private readonly Action invalidAction;
        /// <summary>
        /// 
        /// </summary>
        static DPFCanvas()
        {
            StretchProperty.OverrideMetadata(typeof(DPFCanvas), new FrameworkPropertyMetadata(Stretch.Fill));
        }

        /// <summary>
        /// 
        /// </summary>
        public DPFCanvas()
        {
            renderTimer = new Stopwatch();
            MaxRenderingDuration = TimeSpan.FromMilliseconds(20.0);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            ClearColor = global::SharpDX.Color.Gray;
            IsShadowMapEnabled = false;
        }

        /// <summary>
        /// Invalidates the current render and requests an update.
        /// </summary>
        private void InvalidateRender()
        {
            pendingValidationCycles = true;
        }


        /// <summary>
        /// Invalidates the current render and requests an update.
        /// </summary>
        void IRenderHost.InvalidateRender()
        {
            InvalidateRender();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (IsInDesignMode)
            {
                return;
            }
            loaded = true;
            try
            {
                if (StartD3D())
                { StartRendering(); }
            }
            catch (Exception ex)
            {
                // Exceptions in the Loaded event handler are silently swallowed by WPF.
                // https://social.msdn.microsoft.com/Forums/vstudio/en-US/9ed3d13d-0b9f-48ac-ae8d-daf0845c9e8f/bug-in-wpf-windowloaded-exception-handling?forum=wpf
                // http://stackoverflow.com/questions/19140593/wpf-exception-thrown-in-eventhandler-is-swallowed
                // tl;dr: M$ says it's "by design" and "working as indended" but may change in the future :).

                if (!HandleExceptionOccured(ex))
                {
                    MessageBox.Show(string.Format("DPFCanvas: Error starting rendering: {0}", ex.Message), "Error");
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (IsInDesignMode)
            {
                return;
            }
            loaded = false;
            StopRendering();
            EndD3D(true);
        }

        /// <summary>
        /// 
        /// </summary>
        private bool StartD3D()
        {
            if (!loaded || EffectsManager == null)
            {
                return false; // StardD3D() is called from DP changed handler
            }
            
            surfaceD3D = new DX11ImageSource(EffectsManager.AdapterIndex);
            surfaceD3D.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
            device = EffectsManager.Device;
            Disposer.RemoveAndDispose(ref buffers);
            buffers = new DX11RenderBufferProxy(device);
            Disposer.RemoveAndDispose(ref renderer);
            renderer = new CommonRenderer(device);
            CreateAndBindTargets();
            SetDefaultRenderTargets();
            Source = surfaceD3D;
            InvalidateRender();
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        private void EndD3D(bool dispose)
        {
            DetachRenderables();
            
            if (surfaceD3D == null)
            {
                return;
            }

            surfaceD3D.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
            Source = null;
            renderContext?.Dispose();
            Disposer.RemoveAndDispose(ref renderer);
            Disposer.RemoveAndDispose(ref surfaceD3D);
#if MSAA
            Disposer.RemoveAndDispose(ref renderTargetNMS);
#endif
            Disposer.RemoveAndDispose(ref buffers);
        }

        private void DetachRenderables()
        {
            if (viewport != null && sceneAttached)
            {
                viewport.Detach();
            }
            sceneAttached = false;
        }
        /// <summary>
        /// 
        /// </summary>
        private void CreateAndBindTargets()
        {
            surfaceD3D.SetRenderTargetDX11(null);
            int width = Math.Max((int)ActualWidth, 100);
            int height = Math.Max((int)ActualHeight, 100);

            var colorBuffer = buffers.StartD3D(width, height, MSAA);
#if MSAA
            var colordescNMS = new Texture2DDescription
            {
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                MipLevels = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                OptionFlags = ResourceOptionFlags.Shared,
                CpuAccessFlags = CpuAccessFlags.None,
                ArraySize = 1
            };

            renderTargetNMS = new Texture2D(device, colordescNMS);
            device.ImmediateContext.ResolveSubresource(colorBuffer, 0, renderTargetNMS, 0, Format.B8G8R8A8_UNorm);
            surfaceD3D.SetRenderTargetDX11(renderTargetNMS);
#else
            surfaceD3D.SetRenderTargetDX11(colorBuffer);
#endif
            this.device.ImmediateContext.Rasterizer.SetScissorRectangle(0, 0, width, height);
            renderParameter = new RenderParameter()
            {
                target = ColorBufferView, depthStencil = buffers.DepthStencilBufferView,
                ScissorRegion = new Rectangle(0, 0, width, height), ViewportRegion = new ViewportF(0, 0, width, height)
            };
        }

        /// <summary>
        /// Sets the default render-targets
        /// </summary>
        public void SetDefaultRenderTargets(bool clear = true)
        {
            buffers.SetDefaultRenderTargets(device.ImmediateContext);
            if (clear)
            {
                buffers.ClearRenderTarget(device.ImmediateContext, ClearColor);
            }
        }

        /// <summary>
        /// Clears the buffers with the clear-color
        /// </summary>
        /// <param name="clearBackBuffer"></param>
        /// <param name="clearDepthStencilBuffer"></param>
        internal void ClearRenderTarget(bool clearBackBuffer = true, bool clearDepthStencilBuffer = true)
        {
            buffers.ClearRenderTarget(device.ImmediateContext, ClearColor, clearBackBuffer, clearDepthStencilBuffer);
        }

        private Task Render(TimeSpan timeStamp)
        {
            // ---------------------------------------------------------------------------
            // this part is done only if the scene is not attached
            // it is an attach and init pass for all elements in the scene-graph                
            if (!sceneAttached)
            {
                try
                {
                    sceneAttached = true;
                    ClearColor = viewport.BackgroundColor;
                    IsShadowMapEnabled = viewport.IsShadowMappingEnabled;

                    RenderTechnique = viewport.RenderTechnique == null ? EffectsManager?[DefaultRenderTechniqueNames.Blinn] : viewport.RenderTechnique;


                    renderContext?.Dispose();
                    renderContext = new RenderContext(this, device.ImmediateContext);
                    renderContext.EnableBoundingFrustum = EnableRenderFrustum;
                    if (EnableSharingModelMode && SharedModelContainer != null)
                    {
                        SharedModelContainer.CurrentRenderHost = this;
                        viewport.Attach(SharedModelContainer);
                    }
                    else
                    {
                        viewport.Attach(this);
                    }
                }
                catch (Exception ex)
                {
                    //MessageBox.Show("DPFCanvas: Error attaching element: " + string.Format(ex.Message), "Error");
                    Debug.WriteLine("DPFCanvas: Error attaching element: " + string.Format(ex.Message), "Error");
                    throw;
                }
            }
            renderContext.TimeStamp = timeStamp;
            // ---------------------------------------------------------------------------
            // this part is per frame
            // ---------------------------------------------------------------------------
            if (EnableSharingModelMode && SharedModelContainer != null)
            {
                SharedModelContainer.CurrentRenderHost = this;
                ClearRenderTarget();
            }
            else
            {
                SetDefaultRenderTargets(true);
            }

            renderContext.Camera = viewport.CameraCore;
            renderContext.WorldMatrix = viewport.WorldMatrix;
            var task = renderer.Render(renderContext, viewport.Renderables, renderParameter);                   
#if MSAA
            device.ImmediateContext.ResolveSubresource(buffers.ColorBuffer, 0, renderTargetNMS, 0, Format.B8G8R8A8_UNorm);
#endif
            this.device.ImmediateContext.Flush();
            return task;
        }

        private void StartRendering()
        {
            if (renderTimer.IsRunning)
                return;

            lastRenderingDuration = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
            renderTimer.Start();
        }

        private void StopRendering()
        {
            if (!renderTimer.IsRunning)
                return;

            CompositionTarget.Rendering -= OnRendering;
            renderTimer.Stop();
        }

        /// <summary>
        /// Handles the <see cref="CompositionTarget.Rendering"/> event.
        /// </summary>
        /// <param name="sender">The sender is in fact a the UI <see cref="Dispatcher"/>.</param>
        /// <param name="e">Is in fact <see cref="RenderingEventArgs"/>.</param>
        private void OnRendering(object sender, EventArgs e)
        {
            if (!renderTimer.IsRunning || !IsRendering)
                return;
            UpdateAndRender();
        }

        private readonly FrameRateRegulator skipper = new FrameRateRegulator();
        /// <summary>
        /// Updates and renders the scene.
        /// </summary>
        private void UpdateAndRender()
        {
            if (((pendingValidationCycles && !skipper.IsSkip()) || skipper.DelayTrigger()) && surfaceD3D != null && viewport != null)
            {
                var t0 = renderTimer.Elapsed;

                // Update all renderables before rendering 
                // giving them the chance to invalidate the current render.
                pendingValidationCycles = false;
                viewport.UpdateFPS(t0);
                var t = Render(t0);
                try
                {
                    surfaceD3D.Lock();
                    surfaceD3D.AddDirtyRect(new Int32Rect(0, 0, surfaceD3D.PixelWidth, surfaceD3D.PixelHeight));
                }
                catch (Exception ex)
                {
                    if (!HandleExceptionOccured(ex))
                    {
                        MessageBox.Show(string.Format("DPFCanvas: Error while rendering: {0}", ex.Message), "Error");
                    }
                }
                finally
                {
                    surfaceD3D.Unlock();
                }
                t?.Wait();
                lastRenderingDuration = renderTimer.Elapsed - t0;
                skipper.Push(lastRenderingDuration.TotalMilliseconds);
            }
        }

        private DispatcherOperation resizeOperation = null;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="sizeInfo"></param>
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            if (!loaded)
            {
                return;
            }
            if (resizeOperation != null && resizeOperation.Status == DispatcherOperationStatus.Pending)
            {
                resizeOperation.Abort();
            }
            resizeOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (surfaceD3D != null)
                {
                    try
                    {
                        StopRendering();
                        CreateAndBindTargets();
                        SetDefaultRenderTargets();
                        StartRendering();
                        InvalidateRender();
                    }
                    catch (Exception ex)
                    {
                        if (!HandleExceptionOccured(ex))
                        {
                            MessageBox.Show(string.Format("DPFCanvas: Error while rendering: {0}", ex.Message), "Error");
                        }
                    }
                }
            }));
        }

        /// <summary>
        /// Handles the change of the effects manager.
        /// </summary>
        private void RestartRendering()
        {
            StopRendering();
            EndD3D(false);
            if (loaded)
            {
                if (EffectsManager != null)
                {
                    IsDeferredLighting = (renderTechnique == EffectsManager[DeferredRenderTechniqueNames.Deferred]
                        || renderTechnique == EffectsManager[DeferredRenderTechniqueNames.GBuffer]);
                }
                if (StartD3D())
                { StartRendering(); }
            }
        }

        /// <summary>
        /// Invoked whenever an exception occurs. Stops rendering, frees resources and throws 
        /// </summary>
        /// <param name="exception">The exception that occured.</param>
        /// <returns><c>true</c> if the exception has been handled, <c>false</c> otherwise.</returns>
        private bool HandleExceptionOccured(Exception exception)
        {
            pendingValidationCycles = false;
            StopRendering();
            EndD3D(true);

            var sdxException = exception as SharpDXException;
            if (sdxException != null &&
                (sdxException.Descriptor == global::SharpDX.DXGI.ResultCode.DeviceRemoved || 
                 sdxException.Descriptor == global::SharpDX.DXGI.ResultCode.DeviceReset))
            {
                return true;
            }
            else
            {
                var args = new RelayExceptionEventArgs(exception);
                ExceptionOccurred(this, args);
                return args.Handled;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // this fires when the screensaver kicks in, the machine goes into sleep or hibernate
            // and any other catastrophic losses of the d3d device from WPF's point of view
            if (true.Equals(e.NewValue))
            {
                try
                {
                    // Try to recover from DeviceRemoved/DeviceReset
                    EndD3D(true);
                    RestartRendering();
                }
                catch (Exception ex)
                {
                    if (!HandleExceptionOccured(ex))
                    {
                        MessageBox.Show(string.Format("DPFCanvas: Error while rendering: {0}", ex.Message), "Error");
                    }
                }
            }
            else
            {
                pendingValidationCycles = false;
                StopRendering();
            }
        }
    }
}
