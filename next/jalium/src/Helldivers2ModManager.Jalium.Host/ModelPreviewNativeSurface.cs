using System.ComponentModel;
using System.Runtime.InteropServices;
using Helldivers2ModManager.Models;
using Jalium.UI;
using Jalium.UI.Interop;
using Jalium.UI.Media.Media3D;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ModelPreviewNativeSurface : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const uint PfdDrawToWindow = 0x00000004;
    private const uint PfdSupportOpenGl = 0x00000020;
    private const uint PfdDoubleBuffer = 0x00000001;
    private const uint GlColorBufferBit = 0x00004000;
    private const uint GlDepthBufferBit = 0x00000100;
    private const uint GlDepthTest = 0x0B71;
    private const uint GlCullFace = 0x0B44;
    private const uint GlProjection = 0x1701;
    private const uint GlModelView = 0x1700;
    private const uint GlVertexArray = 0x8074;
    private const uint GlNormalArray = 0x8075;
    private const uint GlTriangles = 0x0004;
    private const uint GlLines = 0x0001;
    private const uint GlFloat = 0x1406;
    private const uint GlUnsignedInt = 0x1405;
    private const uint GlBgra = 0x80E1;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlLighting = 0x0B50;
    private const uint GlLight0 = 0x4000;
    private const uint GlColorMaterial = 0x0B57;
    private const uint GlNormalize = 0x0BA1;
    private const uint GlPosition = 0x1203;
    private const uint GlDiffuse = 0x1201;
    private const uint GlAmbient = 0x1200;
    private const uint GlFrontAndBack = 0x0408;
    private const uint GlAmbientAndDiffuse = 0x1602;
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlTextureCoordArray = 0x8078;
    private const uint GlTextureMinFilter = 0x2801;
    private const uint GlTextureMagFilter = 0x2800;
    private const uint GlLinear = 0x2601;
    private const uint GlRgba = 0x1908;

    private const int WmLeftDown = 0x0201;
    private const int WmLeftUp = 0x0202;
    private const int WmRightDown = 0x0204;
    private const int WmRightUp = 0x0205;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WmCaptureChanged = 0x0215;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int WmSize = 0x0005;

    private IReadOnlyList<DrawMesh> _meshes = [];
    private readonly Dictionary<global::Jalium.UI.Media.Imaging.BitmapSource, uint> _textures =
        new(ReferenceEqualityComparer.Instance);
    private PerspectiveCamera? _camera;
    private Vector3D _center;
    private ModelPreviewPresentationRotation _rotation;
    private Rect3D _bounds = Rect3D.Empty;
    private nint _deviceContext;
    private nint _glContext;
    private bool _disposed;
    private bool _texturesDirty;
    private bool _renderingVisible = true;
    private int _dragMode;
    private int _lastX;
    private int _lastY;

    internal event Action<double, double, bool>? NativeDrag;
    internal event Action<int>? WheelMoved;

    internal bool IsRendererReady => _glContext != nint.Zero;
    internal int MeshCount => _meshes.Count;

    internal ModelPreviewNativeSurface()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void SetScene(IReadOnlyList<ModelPreviewMesh> meshes, Vector3D center,
        ModelPreviewPresentationRotation rotation, Rect3D bounds, PerspectiveCamera camera,
        Func<ModelPreviewMesh, global::Jalium.UI.Media.Media3D.Material>? resolveMaterial = null)
    {
        _meshes = meshes.Where(static mesh => mesh.Positions.Length > 0
                && mesh.Positions.Length % 3 == 0
                && mesh.TriangleIndices.Length > 0
                && mesh.TriangleIndices.Length % 3 == 0
                && mesh.TriangleIndices.All(index => index >= 0
                    && index < mesh.Positions.Length / 3))
            .Select(mesh => new DrawMesh(mesh,
            ResolveTexture(resolveMaterial?.Invoke(mesh)))).ToArray();
        _center = center;
        _rotation = rotation;
        _bounds = bounds;
        _camera = camera;
        _texturesDirty = true;
        Draw();
    }

    private static global::Jalium.UI.Media.Imaging.BitmapSource? ResolveTexture(
        global::Jalium.UI.Media.Media3D.Material? material) =>
        material is DiffuseMaterial { Brush: global::Jalium.UI.Media.ImageBrush
            { ImageSource: global::Jalium.UI.Media.Imaging.BitmapSource bitmap } }
            ? bitmap : null;

    internal void Redraw() => Draw();

    internal void SetRenderingVisible(bool visible)
    {
        _renderingVisible = visible;
        if (Handle != nint.Zero)
            ShowWindow(Handle, visible ? 5 : 0);
    }

    internal void SetPositionOverrides(IReadOnlyDictionary<ModelPreviewMesh, float[]>? positions)
    {
        foreach (var mesh in _meshes)
        {
            mesh.PositionOverride = positions is not null
                && positions.TryGetValue(mesh.Source, out var replacement)
                && replacement.Length == mesh.Source.Positions.Length
                ? replacement : null;
        }
        Draw();
    }

    internal float[]? GetPositionOverride(ModelPreviewMesh mesh) =>
        _meshes.FirstOrDefault(entry => ReferenceEquals(entry.Source, mesh))?.PositionOverride;

    internal unsafe (byte[] Pixels, int Width, int Height) CapturePixels()
    {
        if (_glContext == nint.Zero || Handle == nint.Zero
            || !GetClientRect(Handle, out var rect))
            throw new InvalidOperationException("The native model renderer is unavailable.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            throw new InvalidOperationException("The native model renderer has invalid dimensions.");
        var pixels = new byte[checked(width * height * 4)];
        if (!wglMakeCurrent(_deviceContext, _glContext))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            glReadBuffer(0x0404); // GL_FRONT
            fixed (byte* destination = pixels)
                glReadPixels(0, 0, width, height, GlBgra, GlUnsignedByte, (nint)destination);
        }
        finally { wglMakeCurrent(nint.Zero, nint.Zero); }
        return (pixels, width, height);
    }

    private void OnLoaded(object? sender, RoutedEventArgs args)
    {
        if (_disposed)
            return;
        if (Handle != nint.Zero)
        {
            SetRenderingVisible(true);
            UpdateWindowPos();
            Draw();
            return;
        }
        var owner = Window.GetWindow(this);
        if (owner?.Handle is not { } parent || parent == nint.Zero)
            return;
        var child = BuildWindowCore(new HandleRef(this, parent));
        if (child.Handle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create the model preview window.");
        SetHandle(child.Handle);
        UpdateWindowPos();
        SetRenderingVisible(_renderingVisible);
        Draw();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs args)
    {
        if (Handle != nint.Zero)
            SetWindowPos(Handle, nint.Zero, 0, 0, 0, 0, 0x0080 | 0x0004 | 0x0010);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var child = CreateWindowEx(0, "STATIC", null,
            WsChild | WsVisible | WsClipSiblings, 0, 0, 1, 1,
            hwndParent.Handle, nint.Zero, nint.Zero, nint.Zero);
        if (child == nint.Zero)
            return new HandleRef(this, nint.Zero);
        _deviceContext = GetDC(child);
        var format = new PixelFormatDescriptor
        {
            Size = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
            Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
            PixelType = 0,
            ColorBits = 32,
            DepthBits = 24,
            LayerType = 0,
        };
        var index = ChoosePixelFormat(_deviceContext, ref format);
        if (index == 0 || !SetPixelFormat(_deviceContext, index, ref format)
            || (_glContext = wglCreateContext(_deviceContext)) == nint.Zero)
        {
            DestroyWindowCore(new HandleRef(this, child));
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to initialize native 3D rendering.");
        }
        return new HandleRef(this, child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_glContext != nint.Zero)
        {
            wglMakeCurrent(nint.Zero, nint.Zero);
            wglDeleteContext(_glContext);
            _glContext = nint.Zero;
        }
        _textures.Clear();
        _texturesDirty = true;
        if (_deviceContext != nint.Zero && hwnd.Handle != nint.Zero)
        {
            ReleaseDC(hwnd.Handle, _deviceContext);
            _deviceContext = nint.Zero;
        }
        if (hwnd.Handle != nint.Zero)
            DestroyWindow(hwnd.Handle);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateWindowPos();
        Draw();
        return finalSize;
    }

    protected override void OnSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnSizeChanged(sizeInfo);
        UpdateWindowPos();
        Draw();
    }

    protected override void OnRender(global::Jalium.UI.Media.DrawingContext context)
    {
        UpdateWindowPos();
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam,
        ref bool handled)
    {
        switch (message)
        {
            case WmPaint:
                ValidateRect(hwnd, nint.Zero);
                Draw();
                handled = true;
                break;
            case WmEraseBackground:
                handled = true;
                return 1;
            case WmSize:
                Draw();
                break;
            case WmLeftDown:
            case WmRightDown:
                _dragMode = message == WmLeftDown ? 1 : 2;
                _lastX = unchecked((short)((long)lParam & 0xffff));
                _lastY = unchecked((short)(((long)lParam >> 16) & 0xffff));
                SetCapture(hwnd);
                handled = true;
                break;
            case WmLeftUp:
            case WmRightUp:
            case WmCaptureChanged:
                _dragMode = 0;
                if (message != WmCaptureChanged)
                    ReleaseCapture();
                handled = true;
                break;
            case WmMouseMove when _dragMode != 0:
                var x = unchecked((short)((long)lParam & 0xffff));
                var y = unchecked((short)(((long)lParam >> 16) & 0xffff));
                NativeDrag?.Invoke(x - _lastX, y - _lastY, _dragMode == 2);
                _lastX = x;
                _lastY = y;
                handled = true;
                break;
            case WmMouseWheel:
                WheelMoved?.Invoke(unchecked((short)(((long)wParam >> 16) & 0xffff)));
                handled = true;
                break;
        }
        return nint.Zero;
    }

    private unsafe void RefreshTextures()
    {
        if (!_texturesDirty)
            return;
        foreach (var id in _textures.Values)
        {
            var old = id;
            glDeleteTextures(1, &old);
        }
        _textures.Clear();
        foreach (var mesh in _meshes)
        {
            if (mesh.Texture is not { } bitmap || mesh.FlippedUvs is null
                || _textures.ContainsKey(bitmap))
                continue;
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                continue;
            var pixels = new byte[checked(width * height * 4)];
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            uint id = 0;
            glGenTextures(1, &id);
            glBindTexture(GlTexture2D, id);
            glTexParameteri(GlTexture2D, GlTextureMinFilter, (int)GlLinear);
            glTexParameteri(GlTexture2D, GlTextureMagFilter, (int)GlLinear);
            fixed (byte* source = pixels)
                glTexImage2D(GlTexture2D, 0, (int)GlRgba, width, height, 0,
                    GlBgra, GlUnsignedByte, (nint)source);
            _textures.Add(bitmap, id);
        }
        _texturesDirty = false;
    }

    private unsafe void Draw()
    {
        if (_disposed || _glContext == nint.Zero || Handle == nint.Zero
            || !wglMakeCurrent(_deviceContext, _glContext)
            || !GetClientRect(Handle, out var rect))
            return;
        var width = Math.Max(rect.Right - rect.Left, 1);
        var height = Math.Max(rect.Bottom - rect.Top, 1);
        glViewport(0, 0, width, height);
        glClearColor(0x20 / 255f, 0x25 / 255f, 0x2A / 255f, 1);
        glClear(GlColorBufferBit | GlDepthBufferBit);
        if (_camera is { } camera)
        {
            RefreshTextures();
            glEnable(GlDepthTest);
            glDisable(GlCullFace);
            glMatrixMode(GlProjection);
            glLoadIdentity();
            var near = Math.Max(camera.NearPlaneDistance, 0.001);
            var far = Math.Max(camera.FarPlaneDistance, near + 1);
            var top = near * Math.Tan(camera.FieldOfView * Math.PI / 360);
            var right = top * width / height;
            glFrustum(-right, right, -top, top, near, far);
            glMatrixMode(GlModelView);
            glLoadIdentity();
            var eye = camera.Position;
            var target = eye + camera.LookDirection;
            var up = camera.UpDirection;
            gluLookAt(eye.X, eye.Y, eye.Z, target.X, target.Y, target.Z,
                up.X, up.Y, up.Z);
            DrawGrid();
            glEnable(GlLighting);
            glEnable(GlLight0);
            glEnable(GlColorMaterial);
            glEnable(GlNormalize);
            glColorMaterial(GlFrontAndBack, GlAmbientAndDiffuse);
            float[] lightPosition = [-1, 1, 2, 0];
            float[] diffuse = [0.57f, 0.57f, 0.57f, 1];
            float[] ambient = [0.27f, 0.27f, 0.27f, 1];
            fixed (float* position = lightPosition)
            fixed (float* diffuseColor = diffuse)
            fixed (float* ambientColor = ambient)
            {
                glLightfv(GlLight0, GlPosition, (nint)position);
                glLightfv(GlLight0, GlDiffuse, (nint)diffuseColor);
                glLightfv(GlLight0, GlAmbient, (nint)ambientColor);
            }
            switch (_rotation)
            {
                case ModelPreviewPresentationRotation.PositiveXToPositiveY:
                    glRotated(90, 0, 0, 1);
                    break;
                case ModelPreviewPresentationRotation.NegativeXToPositiveY:
                    glRotated(-90, 0, 0, 1);
                    break;
                case ModelPreviewPresentationRotation.PositiveZToPositiveY:
                    glRotated(-90, 1, 0, 0);
                    break;
                case ModelPreviewPresentationRotation.NegativeZToPositiveY:
                    glRotated(90, 1, 0, 0);
                    break;
            }
            glTranslated(-_center.X, -_center.Y, -_center.Z);
            glColor3f(184 / 255f, 193 / 255f, 202 / 255f);
            glEnableClientState(GlVertexArray);
            foreach (var draw in _meshes)
            {
                var mesh = draw.Source;
                var positionsArray = draw.PositionOverride ?? mesh.Positions;
                if (positionsArray.Length == 0 || mesh.TriangleIndices.Length == 0)
                    continue;
                if (draw.Texture is { } bitmap && draw.FlippedUvs is { } uvs
                    && _textures.TryGetValue(bitmap, out var textureId))
                {
                    glEnable(GlTexture2D);
                    glBindTexture(GlTexture2D, textureId);
                    glEnableClientState(GlTextureCoordArray);
                }
                else
                {
                    glDisable(GlTexture2D);
                    glDisableClientState(GlTextureCoordArray);
                }
                fixed (float* positions = positionsArray)
                fixed (int* indices = mesh.TriangleIndices)
                {
                    glVertexPointer(3, GlFloat, 0, (nint)positions);
                    if (mesh.Normals?.Length == mesh.Positions.Length)
                    {
                        glEnableClientState(GlNormalArray);
                        fixed (float* normals = mesh.Normals)
                        fixed (float* coordinates = draw.FlippedUvs)
                        {
                            if (draw.FlippedUvs is not null && draw.Texture is not null)
                                glTexCoordPointer(2, GlFloat, 0, (nint)coordinates);
                            glNormalPointer(GlFloat, 0, (nint)normals);
                            glDrawElements(GlTriangles, mesh.TriangleIndices.Length,
                                GlUnsignedInt, (nint)indices);
                        }
                        glDisableClientState(GlNormalArray);
                    }
                    else
                    {
                        fixed (float* coordinates = draw.FlippedUvs)
                        {
                            if (draw.FlippedUvs is not null && draw.Texture is not null)
                                glTexCoordPointer(2, GlFloat, 0, (nint)coordinates);
                            glDrawElements(GlTriangles, mesh.TriangleIndices.Length,
                                GlUnsignedInt, (nint)indices);
                        }
                    }
                }
            }
            glDisableClientState(GlVertexArray);
            glDisableClientState(GlTextureCoordArray);
            glDisable(GlTexture2D);
        }
        SwapBuffers(_deviceContext);
        wglMakeCurrent(nint.Zero, nint.Zero);
    }

    private void DrawGrid()
    {
        if (_bounds.IsEmpty || !double.IsFinite(_bounds.SizeX)
            || !double.IsFinite(_bounds.SizeZ))
            return;
        var span = Math.Max(Math.Max(_bounds.SizeX, _bounds.SizeZ), 1);
        var target = span / 6;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(target, 0.001))));
        var normalized = target / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10)
            * magnitude;
        var halfLines = Math.Clamp((int)Math.Ceiling(span * 0.8 / step), 4, 20);
        var extent = step * halfLines;
        var centerX = _bounds.X + _bounds.SizeX / 2;
        var centerZ = _bounds.Z + _bounds.SizeZ / 2;
        var floorY = _bounds.Y - Math.Max(step * 0.01, 0.002);
        glDisable(GlLighting);
        glDisable(GlTexture2D);
        glBegin(GlLines);
        for (var index = -halfLines; index <= halfLines; index++)
        {
            var offset = index * step;
            glColor3f(index == 0 ? 0.88f : 0.32f,
                index == 0 ? 0.43f : 0.38f,
                index == 0 ? 0.46f : 0.43f);
            glVertex3d(centerX - extent, floorY, centerZ + offset);
            glVertex3d(centerX + extent, floorY, centerZ + offset);
            glColor3f(index == 0 ? 0.49f : 0.32f,
                index == 0 ? 0.66f : 0.38f,
                index == 0 ? 1f : 0.43f);
            glVertex3d(centerX + offset, floorY, centerZ - extent);
            glVertex3d(centerX + offset, floorY, centerZ + extent);
        }
        glEnd();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        base.Dispose(disposing);
    }

    private sealed class DrawMesh
    {
        internal ModelPreviewMesh Source { get; }
        internal global::Jalium.UI.Media.Imaging.BitmapSource? Texture { get; }
        internal float[]? FlippedUvs { get; }
        internal float[]? PositionOverride { get; set; }

        internal DrawMesh(ModelPreviewMesh source,
            global::Jalium.UI.Media.Imaging.BitmapSource? texture)
        {
            Source = source;
            Texture = texture;
            if (texture is null || source.TextureCoordinates?.Length != source.VertexCount * 2)
                return;
            FlippedUvs = (float[])source.TextureCoordinates.Clone();
            for (var index = 1; index < FlippedUvs.Length; index += 2)
                FlippedUvs[index] = 1 - FlippedUvs[index];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits;
        public byte RedShift;
        public byte GreenBits;
        public byte GreenShift;
        public byte BlueBits;
        public byte BlueShift;
        public byte AlphaBits;
        public byte AlphaShift;
        public byte AccumBits;
        public byte AccumRedBits;
        public byte AccumGreenBits;
        public byte AccumBlueBits;
        public byte AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask;
        public uint VisibleMask;
        public uint DamageMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string? title,
        int style, int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(nint window, nint rect);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);
    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(nint deviceContext, ref PixelFormatDescriptor format);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPixelFormat(nint deviceContext, int index, ref PixelFormatDescriptor format);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwapBuffers(nint deviceContext);
    [DllImport("opengl32.dll")]
    private static extern nint wglCreateContext(nint deviceContext);
    [DllImport("opengl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglMakeCurrent(nint deviceContext, nint context);
    [DllImport("opengl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglDeleteContext(nint context);
    [DllImport("opengl32.dll")]
    private static extern void glViewport(int x, int y, int width, int height);
    [DllImport("opengl32.dll")]
    private static extern void glClearColor(float red, float green, float blue, float alpha);
    [DllImport("opengl32.dll")]
    private static extern void glClear(uint mask);
    [DllImport("opengl32.dll")]
    private static extern void glEnable(uint capability);
    [DllImport("opengl32.dll")]
    private static extern void glDisable(uint capability);
    [DllImport("opengl32.dll")]
    private static extern void glMatrixMode(uint mode);
    [DllImport("opengl32.dll")]
    private static extern void glLoadIdentity();
    [DllImport("opengl32.dll")]
    private static extern void glFrustum(double left, double right, double bottom,
        double top, double near, double far);
    [DllImport("opengl32.dll")]
    private static extern void glRotated(double angle, double x, double y, double z);
    [DllImport("opengl32.dll")]
    private static extern void glTranslated(double x, double y, double z);
    [DllImport("opengl32.dll")]
    private static extern void glColor3f(float red, float green, float blue);
    [DllImport("opengl32.dll")]
    private static extern void glBegin(uint mode);
    [DllImport("opengl32.dll")]
    private static extern void glEnd();
    [DllImport("opengl32.dll")]
    private static extern void glVertex3d(double x, double y, double z);
    [DllImport("opengl32.dll")]
    private static extern void glColorMaterial(uint face, uint mode);
    [DllImport("opengl32.dll")]
    private static extern void glLightfv(uint light, uint parameter, nint values);
    [DllImport("opengl32.dll")]
    private static extern void glEnableClientState(uint array);
    [DllImport("opengl32.dll")]
    private static extern void glDisableClientState(uint array);
    [DllImport("opengl32.dll")]
    private static extern void glVertexPointer(int size, uint type, int stride, nint pointer);
    [DllImport("opengl32.dll")]
    private static extern void glNormalPointer(uint type, int stride, nint pointer);
    [DllImport("opengl32.dll")]
    private static extern void glTexCoordPointer(int size, uint type, int stride, nint pointer);
    [DllImport("opengl32.dll")]
    private static extern void glDrawElements(uint mode, int count, uint type, nint indices);
    [DllImport("opengl32.dll")]
    private static extern unsafe void glGenTextures(int count, uint* textures);
    [DllImport("opengl32.dll")]
    private static extern unsafe void glDeleteTextures(int count, uint* textures);
    [DllImport("opengl32.dll")]
    private static extern void glBindTexture(uint target, uint texture);
    [DllImport("opengl32.dll")]
    private static extern void glTexParameteri(uint target, uint parameter, int value);
    [DllImport("opengl32.dll")]
    private static extern void glTexImage2D(uint target, int level, int internalFormat,
        int width, int height, int border, uint format, uint type, nint pixels);
    [DllImport("opengl32.dll")]
    private static extern void glReadBuffer(uint mode);
    [DllImport("opengl32.dll")]
    private static extern void glReadPixels(int x, int y, int width, int height,
        uint format, uint type, nint pixels);
    [DllImport("glu32.dll")]
    private static extern void gluLookAt(double eyeX, double eyeY, double eyeZ,
        double centerX, double centerY, double centerZ,
        double upX, double upY, double upZ);
}
