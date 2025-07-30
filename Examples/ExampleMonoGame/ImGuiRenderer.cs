using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ExampleMonoGame;

public class ImGuiRenderer
{
    private const float WHEEL_DELTA = 120;

    private readonly Game _game;

    // Graphics
    private readonly GraphicsDevice _graphicsDevice;
    private BasicEffect _effect;
    private readonly RasterizerState _rasterizerState;

    private byte[] _vertexData;
    private VertexBuffer _vertexBuffer;
    private int _vertexBufferSize;

    private byte[] _indexData;
    private IndexBuffer _indexBuffer;
    private int _indexBufferSize;

    // Textures
    private readonly Dictionary<ImTextureID, TextureInfo> _textures;
    private int _nextTexId;

    // Input
    private int _scrollWheelValue;
    private int _horizontalScrollWheelValue;
    private readonly Keys[] _allKeys = Enum.GetValues<Keys>();


    public ImGuiRenderer(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        ImGuiContextPtr context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        _game = game;
        _graphicsDevice = game.GraphicsDevice;
        _textures = new Dictionary<ImTextureID, TextureInfo>();

        _rasterizerState = new RasterizerState()
        {
            CullMode = CullMode.None,
            DepthBias = 0,
            FillMode = FillMode.Solid,
            MultiSampleAntiAlias = false,
            ScissorTestEnable = true,
            SlopeScaleDepthBias = 0
        };

        SetupInput();
        SetupBackendCapabilities();
    }

    private void SetupBackendCapabilities()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;

        // Set up platform IO for texture management
        ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();

        if (_graphicsDevice.GraphicsProfile == GraphicsProfile.Reach)
        {
            platformIO.RendererTextureMaxWidth = 2048;
            platformIO.RendererTextureMaxHeight = 2048;
        }
        else
        {
            platformIO.RendererTextureMaxWidth = 4096;
            platformIO.RendererTextureMaxHeight = 4096;
        }
    }

    public virtual unsafe ImTextureRef BindTexture(Texture2D texture)
    {
        IntPtr texId = new IntPtr(_nextTexId++);

        TextureInfo textureInfo = new TextureInfo()
        {
            Texture = texture,
            IsManaged = false,
        };

        _textures[texId] = textureInfo;

        return new ImTextureRef(null, texId);
    }

    public virtual void UnbindTexture(ImTextureRef textureRef)
    {
        if (_textures.TryGetValue(textureRef.TexID, out TextureInfo textureInfo))
        {
            if (textureInfo.IsManaged)
            {
                textureInfo.Texture?.Dispose();
            }
            _textures.Remove(textureRef.TexID);
        }
    }

    public virtual void UpdateTexture(ImTextureDataPtr textureData)
    {
        switch (textureData.Status)
        {
            case ImTextureStatus.WantCreate:
                CreateTexture(textureData);
                break;

            case ImTextureStatus.WantUpdates:
                UpdateTextureData(textureData);
                break;

            case ImTextureStatus.WantDestroy:
                DestroyTexture(textureData);
                break;

            case ImTextureStatus.Ok:
                // Nothing to do
                break;
        }
    }

    private unsafe void CreateTexture(ImTextureDataPtr textureData)
    {
        SurfaceFormat format = textureData.Format == ImTextureFormat.Rgba32 ? SurfaceFormat.Color : SurfaceFormat.Alpha8;
        Texture2D texture = new Texture2D(_graphicsDevice, textureData.Width, textureData.Height, false, format);

        if (textureData.Pixels != null)
        {
            var pixelCount = textureData.Width * textureData.Height;
            var bytesPerPixel = textureData.Format == ImTextureFormat.Rgba32 ? 4 : 1;
            var dataSize = pixelCount * bytesPerPixel;

            var managedData = new byte[dataSize];
            Marshal.Copy(new IntPtr(textureData.Pixels), managedData, 0, dataSize);
            texture.SetData(managedData);
        }

        TextureInfo textureInfo = new TextureInfo()
        {
            Texture = texture,
            IsManaged = true,
        };

        _textures[textureData.TexID] = textureInfo;
        textureData.SetStatus(ImTextureStatus.Ok);
    }

    private unsafe void UpdateTextureData(ImTextureDataPtr textureData)
    {
        IntPtr texId = textureData.GetTexID();
        if (!_textures.TryGetValue(texId, out var textureInfo))
        {
            return;
        }

        Texture2D texture = textureInfo.Texture;

        // Check if the texture's dimensions or format have changed
        SurfaceFormat newFormat = textureData.Format == ImTextureFormat.Rgba32 ? SurfaceFormat.Color : SurfaceFormat.Alpha8;
        if (texture.Width != textureData.Width || texture.Height != textureData.Height || texture.Format != newFormat)
        {
            texture.Dispose();
            texture = new Texture2D(_graphicsDevice, textureData.Width, textureData.Height, false, newFormat);
            textureInfo.Texture = texture;
        }

        // TODO: Look into doing only partial updates with textureData.Updates
        //       for now, just doing a full copy
        if (textureData.Pixels != null)
        {
            int pixelCount = textureData.Width * textureData.Height;
            int bytesPerPixel = textureData.Format == ImTextureFormat.Rgba32 ? 4 : 1;
            int dataSize = pixelCount * bytesPerPixel;

            byte[] managedData = new byte[dataSize];
            Marshal.Copy(new IntPtr(textureData.Pixels), managedData, 0, dataSize);
            texture.SetData(managedData);
        }

        textureData.SetStatus(ImTextureStatus.Ok);
    }

    private void DestroyTexture(ImTextureDataPtr textureData)
    {
        IntPtr texId = textureData.GetTexID();
        if (_textures.TryGetValue(texId, out TextureInfo textureInfo))
        {
            if (textureInfo.IsManaged)
            {
                textureInfo.Texture?.Dispose();
            }
            _textures.Remove(texId);
        }
    }

    public virtual void BeforeLayout(GameTime gameTime)
    {
        ImGui.GetIO().DeltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;
        UpdateInput();
        ImGui.NewFrame();
    }

    public virtual void AfterLayout()
    {
        ImGui.Render();

        unsafe
        {
            ImDrawDataPtr drawData = ImGui.GetDrawData();
            ProcessTextureUpdates(drawData);
            RenderDrawData(drawData);
        }
    }

    private unsafe void ProcessTextureUpdates(ImDrawDataPtr drawData)
    {
        if (drawData.Textures.Data == null) return;

        for (int i = 0; i < drawData.Textures.Size; i++)
        {
            ImTextureDataPtr textureData = drawData.Textures.Data[i];
            UpdateTexture(textureData);
        }
    }

    protected virtual void SetupInput()
    {
        ImGuiIOPtr io = ImGui.GetIO();

        //////////////////////////////////////////////
        // MonoGame Specific
        _game.Window.TextInput += (s, a) =>
        {
            if (a.Character == '\t')
            {
                return;
            }

            io.AddInputCharacter(a.Character);
        };
        //////////////////////////////////////////////

        //////////////////////////////////////////////
        // FNA-specific
        // TextInputEx.TextInput += c =>
        // {
        //     if (c == '\t')
        //     {
        //         return;
        //     }
        //     io.AddInputCharacter(c);
        // };
        //////////////////////////////////////////////
    }

    protected virtual Effect UpdateEffect(Texture2D texture)
    {
        ImGuiIOPtr io = ImGui.GetIO();

        _effect ??= new BasicEffect(_graphicsDevice);

        _effect.World = Matrix.Identity;
        _effect.View = Matrix.Identity;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0.0f, io.DisplaySize.X, io.DisplaySize.Y, 0.0f, -1.0f, 1.0f);
        _effect.TextureEnabled = true;
        _effect.Texture = texture;
        _effect.VertexColorEnabled = true;

        return _effect;
    }

    protected virtual void UpdateInput()
    {
        if (!_game.IsActive)
        {
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        MouseState mouse = Mouse.GetState();
        KeyboardState keyboard = Keyboard.GetState();

        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse.LeftButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(1, mouse.RightButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(2, mouse.MiddleButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(3, mouse.XButton1 == ButtonState.Pressed);
        io.AddMouseButtonEvent(4, mouse.XButton2 == ButtonState.Pressed);

        float mouseWheelX = (mouse.HorizontalScrollWheelValue - _horizontalScrollWheelValue) / WHEEL_DELTA;
        float mouseWheelY = (mouse.ScrollWheelValue - _scrollWheelValue) / WHEEL_DELTA;
        io.AddMouseWheelEvent(mouseWheelX, mouseWheelY);

        _scrollWheelValue = mouse.ScrollWheelValue;
        _horizontalScrollWheelValue = mouse.HorizontalScrollWheelValue;

        foreach (Keys key in _allKeys)
        {
            if (TryMapKeys(key, out ImGuiKey imguiKey))
            {
                io.AddKeyEvent(imguiKey, keyboard.IsKeyDown(key));
            }
        }

        int backBufferWidth = _graphicsDevice.PresentationParameters.BackBufferWidth;
        int backBufferHeight = _graphicsDevice.PresentationParameters.BackBufferHeight;
        io.DisplaySize = new System.Numerics.Vector2(backBufferWidth, backBufferHeight);

        io.DisplayFramebufferScale = System.Numerics.Vector2.One;
    }

    private bool TryMapKeys(Keys key, out ImGuiKey imguiKey)
    {
        // Special case not handled in the switch..
        // If the actual key we put in is "None", return none and true;
        // otherwise, return none and false.
        if (key == Keys.None)
        {
            imguiKey = ImGuiKey.None;
            return true;
        }

        imguiKey = key switch
        {
            Keys.Back => ImGuiKey.Backspace,
            Keys.Tab => ImGuiKey.Tab,
            Keys.Enter => ImGuiKey.Enter,
            Keys.CapsLock => ImGuiKey.CapsLock,
            Keys.Escape => ImGuiKey.Escape,
            Keys.Space => ImGuiKey.Space,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.End => ImGuiKey.End,
            Keys.Home => ImGuiKey.Home,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PrintScreen => ImGuiKey.PrintScreen,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            >= Keys.D0 and <= Keys.D9 => ImGuiKey.Key0 + (key - Keys.D0),
            >= Keys.A and <= Keys.Z => ImGuiKey.A + (key - Keys.A),
            >= Keys.NumPad0 and <= Keys.NumPad9 => ImGuiKey.Keypad0 + (key - Keys.NumPad0),
            Keys.Multiply => ImGuiKey.KeypadMultiply,
            Keys.Add => ImGuiKey.KeypadAdd,
            Keys.Subtract => ImGuiKey.KeypadSubtract,
            Keys.Decimal => ImGuiKey.KeypadDecimal,
            Keys.Divide => ImGuiKey.KeypadDivide,
            >= Keys.F1 and <= Keys.F24 => ImGuiKey.F1 + (key - Keys.F1),
            Keys.NumLock => ImGuiKey.NumLock,
            Keys.Scroll => ImGuiKey.ScrollLock,
            Keys.LeftShift => ImGuiKey.ModShift,
            Keys.LeftControl => ImGuiKey.ModCtrl,
            Keys.LeftAlt => ImGuiKey.ModAlt,
            Keys.OemSemicolon => ImGuiKey.Semicolon,
            Keys.OemPlus => ImGuiKey.Equal,
            Keys.OemComma => ImGuiKey.Comma,
            Keys.OemMinus => ImGuiKey.Minus,
            Keys.OemPeriod => ImGuiKey.Period,
            Keys.OemQuestion => ImGuiKey.Slash,
            Keys.OemTilde => ImGuiKey.GraveAccent,
            Keys.OemOpenBrackets => ImGuiKey.LeftBracket,
            Keys.OemCloseBrackets => ImGuiKey.RightBracket,
            Keys.OemPipe => ImGuiKey.Backslash,
            Keys.OemQuotes => ImGuiKey.Apostrophe,
            Keys.BrowserBack => ImGuiKey.AppBack,
            Keys.BrowserForward => ImGuiKey.AppForward,
            _ => ImGuiKey.None
        };

        return imguiKey != ImGuiKey.None;
    }

    private unsafe void RenderDrawData(ImDrawData* drawData)
    {
        // Cache states so they can be restored when we're done.
        Viewport lastViewport = _graphicsDevice.Viewport;
        Rectangle lastScissorBox = _graphicsDevice.ScissorRectangle;
        RasterizerState lastRasterizer = _graphicsDevice.RasterizerState;
        DepthStencilState lastDepthStencil = _graphicsDevice.DepthStencilState;
        Color lastBlendFactor = _graphicsDevice.BlendFactor;
        BlendState lastBlendState = _graphicsDevice.BlendState;
        SamplerState lastSamplerState = _graphicsDevice.SamplerStates[0];

        // Setup render state: 
        // - alpha-blending enabled
        _graphicsDevice.BlendFactor = Color.White;
        _graphicsDevice.BlendState = BlendState.NonPremultiplied;

        // - No face culling  
        // - Scissor testing enabled
        _graphicsDevice.RasterizerState = _rasterizerState;

        // - Depth read-only (testing enabled, writes disabled)
        _graphicsDevice.DepthStencilState = DepthStencilState.DepthRead;

        // - Point filtering for textures (no interpolation)
        _graphicsDevice.SamplerStates[0] = SamplerState.PointClamp;

        // Handle cases of screen coordinates != from framebuffer coordinates (e.g. retina displays)
        drawData->ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        // Setup projection
        _graphicsDevice.Viewport = new Viewport(0, 0, _graphicsDevice.PresentationParameters.BackBufferWidth, _graphicsDevice.PresentationParameters.BackBufferHeight);

        UpdateBuffers(drawData);

        RenderCommandLists(drawData);

        // Restore modified state
        _graphicsDevice.Viewport = lastViewport;
        _graphicsDevice.ScissorRectangle = lastScissorBox;
        _graphicsDevice.RasterizerState = lastRasterizer;
        _graphicsDevice.DepthStencilState = lastDepthStencil;
        _graphicsDevice.BlendState = lastBlendState;
        _graphicsDevice.BlendFactor = lastBlendFactor;
        _graphicsDevice.SamplerStates[0] = lastSamplerState;
    }

    private unsafe void UpdateBuffers(ImDrawData* drawData)
    {
        if (drawData->TotalVtxCount == 0)
        {
            return;
        }

        // Expand buffers if we need more room
        if (drawData->TotalVtxCount > _vertexBufferSize)
        {
            _vertexBuffer?.Dispose();

            _vertexBufferSize = (int)(drawData->TotalVtxCount * 1.5f);
            _vertexBuffer = new VertexBuffer(_graphicsDevice, DrawVertDeclaration.Declaration, _vertexBufferSize, BufferUsage.None);
            _vertexData = new byte[_vertexBufferSize * DrawVertDeclaration.Size];
        }

        if (drawData->TotalIdxCount > _indexBufferSize)
        {
            _indexBuffer?.Dispose();

            _indexBufferSize = (int)(drawData->TotalIdxCount * 1.5f);
            _indexBuffer = new IndexBuffer(_graphicsDevice, IndexElementSize.SixteenBits, _indexBufferSize, BufferUsage.None);
            _indexData = new byte[_indexBufferSize * sizeof(ushort)];
        }

        // Copy ImGui's vertices and indices to a set of managed byte arrays
        int vtxOffset = 0;
        int idxOffset = 0;

        for (int n = 0; n < drawData->CmdListsCount; n++)
        {
            ImDrawList* cmdList = drawData->CmdLists.Data[n];

            fixed (void* vtxDstPtr = &_vertexData[vtxOffset * DrawVertDeclaration.Size])
            {
                fixed (void* idxDstPtr = &_indexData[idxOffset * sizeof(ushort)])
                {
                    Buffer.MemoryCopy(cmdList->VtxBuffer.Data, vtxDstPtr, _vertexData.Length, cmdList->VtxBuffer.Size * DrawVertDeclaration.Size);
                    Buffer.MemoryCopy(cmdList->IdxBuffer.Data, idxDstPtr, _indexData.Length, cmdList->IdxBuffer.Size * sizeof(ushort));
                }
            }

            vtxOffset += cmdList->VtxBuffer.Size;
            idxOffset += cmdList->IdxBuffer.Size;
        }

        // Copy the managed byte arrays to the gpu vertex- and index buffers
        _vertexBuffer.SetData(_vertexData, 0, drawData->TotalVtxCount * DrawVertDeclaration.Size);
        _indexBuffer.SetData(_indexData, 0, drawData->TotalIdxCount * sizeof(ushort));
    }

    private unsafe void RenderCommandLists(ImDrawData* drawData)
    {
        _graphicsDevice.SetVertexBuffer(_vertexBuffer);
        _graphicsDevice.Indices = _indexBuffer;

        int vtxOffset = 0;
        int idxOffset = 0;

        for (int n = 0; n < drawData->CmdListsCount; n++)
        {
            ImDrawList* cmdList = drawData->CmdLists.Data[n];

            for (int cmdi = 0; cmdi < cmdList->CmdBuffer.Size; cmdi++)
            {
                ImDrawCmd* drawCmd = &cmdList->CmdBuffer.Data[cmdi];

                if (drawCmd->ElemCount == 0)
                {
                    continue;
                }

                // In v1.92, we need to handle ImTextureRef instead of ImTextureID
                ImTextureRef textureRef = drawCmd->TexRef;
                ImTextureID texId = textureRef.GetTexID();
                if (!_textures.TryGetValue(texId, out var textureInfo))
                {
                    throw new InvalidOperationException($"Could not find a texture with id '{texId}', please check your bindings");
                }

                _graphicsDevice.ScissorRectangle = new Rectangle(
                    (int)drawCmd->ClipRect.X,
                    (int)drawCmd->ClipRect.Y,
                    (int)(drawCmd->ClipRect.Z - drawCmd->ClipRect.X),
                    (int)(drawCmd->ClipRect.W - drawCmd->ClipRect.Y)
                );

                var effect = UpdateEffect(textureInfo.Texture);

                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();

#pragma warning disable CS0618 // // FNA does not expose an alternative method.
                    _graphicsDevice.DrawIndexedPrimitives(
                        primitiveType: PrimitiveType.TriangleList,
                        baseVertex: (int)drawCmd->VtxOffset + vtxOffset,
                        minVertexIndex: 0,
                        numVertices: cmdList->VtxBuffer.Size,
                        startIndex: (int)drawCmd->IdxOffset + idxOffset,
                        primitiveCount: (int)drawCmd->ElemCount / 3
                    );
#pragma warning restore CS0618
                }
            }

            vtxOffset += cmdList->VtxBuffer.Size;
            idxOffset += cmdList->IdxBuffer.Size;
        }
    }

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _effect?.Dispose();

        // Clean up managed textures
        foreach (var kvp in _textures)
        {
            if (kvp.Value.IsManaged)
            {
                kvp.Value.Texture?.Dispose();
            }
        }
        _textures.Clear();

        ImGui.DestroyContext();
    }
}
