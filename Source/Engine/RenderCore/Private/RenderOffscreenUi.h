#pragma once

#include <RmlUi/Core.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <memory>
#include <unordered_map>

// RmlUi consumes the same geometry, fonts and file resolver as the windowed
// hosts. Only rasterization is different; this renderer owns CPU resources.
class RenderBackend_Offscreen::UiRenderer final : public Rml::RenderInterface
{
public:
    explicit UiRenderer(RenderBackend_Offscreen& Backend) : Backend(Backend) {}

    void BeginFrame()
    {
        DrawCalls = 0;
        ScissorEnabled = false;
        MaskEnabled = false;
        Transform = Rml::Matrix4f::Identity();
        Layers.clear();
        LayerStack.clear();
    }

    Rml::CompiledGeometryHandle CompileGeometry(Rml::Span<const Rml::Vertex> Vertices,
        Rml::Span<const int> Indices) override
    {
        auto Data = std::make_unique<Geometry>();
        Data->Vertices.assign(Vertices.begin(), Vertices.end());
        Data->Indices.assign(Indices.begin(), Indices.end());
        const auto Handle = reinterpret_cast<Rml::CompiledGeometryHandle>(Data.get());
        Geometries.emplace(Handle, std::move(Data));
        return Handle;
    }

    void ReleaseGeometry(Rml::CompiledGeometryHandle Handle) override { Geometries.erase(Handle); }

    void RenderGeometry(Rml::CompiledGeometryHandle Handle, Rml::Vector2f Translation,
        Rml::TextureHandle TextureHandle) override
    {
        const auto Found = Geometries.find(Handle);
        if (Found == Geometries.end())
        {
            return;
        }
        ++DrawCalls;
        const auto TextureFound = Textures.find(TextureHandle);
        const auto* Image = TextureFound == Textures.end() ? nullptr : TextureFound->second.get();
        auto& Target = CurrentPixels();
        Rasterize(*Found->second, Translation, [&](size_t Pixel, const Rml::Vertex& A,
            const Rml::Vertex& B, const Rml::Vertex& C, float W0, float W1, float W2)
        {
            if (MaskEnabled && (Pixel >= Mask.size() || !Mask[Pixel]))
            {
                return;
            }
            std::array<float, 4> Source;
            for (int Channel = 0; Channel < 4; ++Channel)
            {
                Source[Channel] = W0 * A.colour[Channel] + W1 * B.colour[Channel] + W2 * C.colour[Channel];
            }
            if (Image)
            {
                const auto UV = A.tex_coord * W0 + B.tex_coord * W1 + C.tex_coord * W2;
                const auto Texel = Sample(*Image, UV);
                for (int Channel = 0; Channel < 4; ++Channel)
                {
                    Source[Channel] *= Texel[Channel] / 255.0f;
                }
            }
            Blend(Target, Pixel, Source);
        });
    }

    Rml::TextureHandle GenerateTexture(Rml::Span<const Rml::byte> Source,
        Rml::Vector2i Dimensions) override
    {
        if (Dimensions.x <= 0 || Dimensions.y <= 0 ||
            Source.size() != size_t(Dimensions.x) * size_t(Dimensions.y) * 4)
        {
            return 0;
        }
        auto Image = std::make_unique<Texture>();
        Image->Width = Dimensions.x;
        Image->Height = Dimensions.y;
        Image->Pixels.assign(Source.begin(), Source.end());
        const auto Handle = reinterpret_cast<Rml::TextureHandle>(Image.get());
        Textures.emplace(Handle, std::move(Image));
        return Handle;
    }

    Rml::TextureHandle LoadTexture(Rml::Vector2i& Dimensions, const Rml::String& Source) override
    {
        auto* Files = Rml::GetFileInterface();
        const auto File = Files->Open(Source);
        if (!File)
        {
            return 0;
        }
        Files->Seek(File, 0, SEEK_END);
        const auto Size = Files->Tell(File);
        Files->Seek(File, 0, SEEK_SET);
        std::vector<Rml::byte> Bytes(Size);
        const auto Read = Files->Read(Bytes.data(), Size, File);
        Files->Close(File);
        if (Read != Size || Size < 18)
        {
            return 0;
        }
        const int Width = Bytes[12] | (Bytes[13] << 8);
        const int Height = Bytes[14] | (Bytes[15] << 8);
        const int Channels = Bytes[16] / 8;
        const size_t Start = 18 + Bytes[0];
        if (Bytes[1] != 0 || Bytes[2] != 2 || Width <= 0 || Height <= 0 ||
            (Channels != 3 && Channels != 4) || Start + size_t(Width) * Height * Channels > Size)
        {
            EngineLog::Error("RenderOffscreen", "UI_TEXTURE_UNSUPPORTED: expected uncompressed 24/32-bit TGA: " + Source);
            return 0;
        }
        std::vector<Rml::byte> RGBA(size_t(Width) * Height * 4);
        for (int Y = 0; Y < Height; ++Y)
        {
            for (int X = 0; X < Width; ++X)
            {
                const int SourceY = (Bytes[17] & 32) ? Y : Height - 1 - Y;
                const int SourceX = (Bytes[17] & 16) ? Width - 1 - X : X;
                const size_t Input = Start + (size_t(SourceY) * Width + SourceX) * Channels;
                const size_t Output = (size_t(Y) * Width + X) * 4;
                const auto Alpha = Channels == 4 ? Bytes[Input + 3] : 255;
                RGBA[Output] = Rml::byte(Bytes[Input + 2] * Alpha / 255);
                RGBA[Output + 1] = Rml::byte(Bytes[Input + 1] * Alpha / 255);
                RGBA[Output + 2] = Rml::byte(Bytes[Input] * Alpha / 255);
                RGBA[Output + 3] = Rml::byte(Alpha);
            }
        }
        Dimensions = Rml::Vector2i(Width, Height);
        return GenerateTexture(RGBA, Dimensions);
    }

    void ReleaseTexture(Rml::TextureHandle Handle) override { Textures.erase(Handle); }
    void EnableScissorRegion(bool Enable) override { ScissorEnabled = Enable; }
    void SetScissorRegion(Rml::Rectanglei Region) override { Scissor = Region; }
    void SetTransform(const Rml::Matrix4f* Value) override { Transform = Value ? *Value : Rml::Matrix4f::Identity(); }
    void EnableClipMask(bool Enable) override { MaskEnabled = Enable; }

    void RenderToClipMask(Rml::ClipMaskOperation Operation, Rml::CompiledGeometryHandle Handle,
        Rml::Vector2f Translation) override
    {
        const auto Found = Geometries.find(Handle);
        if (Found == Geometries.end())
        {
            return;
        }
        std::vector<uint8_t> Coverage(size_t(Backend.Width) * Backend.Height,
            Operation == Rml::ClipMaskOperation::SetInverse ? 1 : 0);
        Rasterize(*Found->second, Translation, [&](size_t Pixel, const Rml::Vertex&,
            const Rml::Vertex&, const Rml::Vertex&, float, float, float)
        {
            Coverage[Pixel] = Operation == Rml::ClipMaskOperation::SetInverse ? 0 : 1;
        });
        if (Operation == Rml::ClipMaskOperation::Intersect && Mask.size() == Coverage.size())
        {
            for (size_t Index = 0; Index < Coverage.size(); ++Index)
            {
                Coverage[Index] &= Mask[Index];
            }
        }
        Mask = std::move(Coverage);
    }

    Rml::LayerHandle PushLayer() override
    {
        const auto Handle = ++NextLayer;
        Layers.emplace(Handle, std::vector<uint8_t>(Backend.Pixels.size(), 0));
        LayerStack.push_back(Handle);
        return Handle;
    }

    void PopLayer() override
    {
        if (!LayerStack.empty())
        {
            Layers.erase(LayerStack.back());
            LayerStack.pop_back();
        }
    }

    void CompositeLayers(Rml::LayerHandle Source, Rml::LayerHandle Destination,
        Rml::BlendMode Mode, Rml::Span<const Rml::CompiledFilterHandle> Handles) override
    {
        auto Pixels = LayerPixels(Source);
        for (const auto Handle : Handles)
        {
            const auto Found = Filters.find(Handle);
            if (Found != Filters.end())
            {
                ApplyFilter(Pixels, *Found->second);
            }
        }
        auto& Target = LayerPixels(Destination);
        const auto Bounds = ClipBounds();
        for (int Y = Bounds.Top(); Y < Bounds.Bottom(); ++Y)
        {
            for (int X = Bounds.Left(); X < Bounds.Right(); ++X)
            {
                const size_t Pixel = size_t(Y) * Backend.Width + X;
                if (MaskEnabled && (Pixel >= Mask.size() || !Mask[Pixel]))
                {
                    continue;
                }
                if (Mode == Rml::BlendMode::Replace)
                {
                    std::copy_n(Pixels.data() + Pixel * 4, 4, Target.data() + Pixel * 4);
                }
                else
                {
                    Blend(Target, Pixel, {float(Pixels[Pixel * 4]), float(Pixels[Pixel * 4 + 1]),
                        float(Pixels[Pixel * 4 + 2]), float(Pixels[Pixel * 4 + 3])});
                }
            }
        }
    }

    Rml::TextureHandle SaveLayerAsTexture() override
    {
        const auto Bounds = ClipBounds();
        const auto& Pixels = CurrentPixels();
        std::vector<Rml::byte> Cropped(size_t(Bounds.Width()) * Bounds.Height() * 4);
        for (int Y = 0; Y < Bounds.Height(); ++Y)
        {
            std::copy_n(Pixels.data() + (size_t(Y + Bounds.Top()) * Backend.Width + Bounds.Left()) * 4,
                size_t(Bounds.Width()) * 4, Cropped.data() + size_t(Y) * Bounds.Width() * 4);
        }
        return GenerateTexture(Cropped, Bounds.Size());
    }

    Rml::CompiledFilterHandle CompileFilter(const Rml::String& Name, const Rml::Dictionary& Parameters) override
    {
        if (Name != "opacity" && Name != "blur")
        {
            EngineLog::Error("RenderOffscreen", "UI_FILTER_UNSUPPORTED: " + Name);
            return 0;
        }
        auto Data = std::make_unique<Filter>();
        Data->Name = Name;
        const auto Found = Parameters.find(Name == "blur" ? "sigma" : "value");
        Data->Value = Found == Parameters.end() ? 1.0f : Found->second.Get<float>();
        const auto Handle = reinterpret_cast<Rml::CompiledFilterHandle>(Data.get());
        Filters.emplace(Handle, std::move(Data));
        return Handle;
    }

    void ReleaseFilter(Rml::CompiledFilterHandle Handle) override { Filters.erase(Handle); }

    Rml::CompiledShaderHandle CompileShader(const Rml::String& Name, const Rml::Dictionary&) override
    {
        EngineLog::Error("RenderOffscreen", "UI_SHADER_UNSUPPORTED: " + Name);
        return 0;
    }

    uint32_t DrawCalls = 0;

private:
    struct Geometry
    {
        std::vector<Rml::Vertex> Vertices;
        std::vector<int> Indices;
    };
    struct Texture
    {
        int Width = 0;
        int Height = 0;
        std::vector<uint8_t> Pixels;
    };
    struct Filter
    {
        Rml::String Name;
        float Value = 1;
    };
    RenderBackend_Offscreen& Backend;
    Rml::Matrix4f Transform = Rml::Matrix4f::Identity();
    bool ScissorEnabled = false;
    bool MaskEnabled = false;
    Rml::Rectanglei Scissor;
    std::vector<uint8_t> Mask;
    std::unordered_map<Rml::CompiledGeometryHandle, std::unique_ptr<Geometry>> Geometries;
    std::unordered_map<Rml::TextureHandle, std::unique_ptr<Texture>> Textures;
    std::unordered_map<Rml::CompiledFilterHandle, std::unique_ptr<Filter>> Filters;
    std::unordered_map<Rml::LayerHandle, std::vector<uint8_t>> Layers;
    std::vector<Rml::LayerHandle> LayerStack;
    Rml::LayerHandle NextLayer = 0;

    std::vector<uint8_t>& LayerPixels(Rml::LayerHandle Handle) { return Handle == 0 ? Backend.Pixels : Layers.at(Handle); }
    std::vector<uint8_t>& CurrentPixels() { return LayerPixels(LayerStack.empty() ? 0 : LayerStack.back()); }

    Rml::Rectanglei ClipBounds() const
    {
        const auto Viewport = Rml::Rectanglei::FromSize(Rml::Vector2i(Backend.Width, Backend.Height));
        return ScissorEnabled ? Viewport.Intersect(Scissor) : Viewport;
    }

    static uint8_t Byte(float Value) { return uint8_t(std::lround(std::clamp(Value, 0.0f, 255.0f))); }

    static void Blend(std::vector<uint8_t>& Pixels, size_t Pixel, const std::array<float, 4>& Source)
    {
        const float Inverse = 1 - Source[3] / 255.0f;
        for (int Channel = 0; Channel < 4; ++Channel)
        {
            const auto Offset = Pixel * 4 + Channel;
            Pixels[Offset] = Byte(Source[Channel] + Pixels[Offset] * Inverse);
        }
    }

    static std::array<float, 4> Sample(const Texture& Image, Rml::Vector2f UV)
    {
        const float X = std::clamp(UV.x * Image.Width - 0.5f, 0.0f, float(Image.Width - 1));
        const float Y = std::clamp(UV.y * Image.Height - 0.5f, 0.0f, float(Image.Height - 1));
        const int X0 = int(X);
        const int Y0 = int(Y);
        const int X1 = (std::min)(X0 + 1, Image.Width - 1);
        const int Y1 = (std::min)(Y0 + 1, Image.Height - 1);
        std::array<float, 4> Result;
        for (int Channel = 0; Channel < 4; ++Channel)
        {
            const float Top = std::lerp(float(Image.Pixels[(size_t(Y0) * Image.Width + X0) * 4 + Channel]),
                float(Image.Pixels[(size_t(Y0) * Image.Width + X1) * 4 + Channel]), X - X0);
            const float Bottom = std::lerp(float(Image.Pixels[(size_t(Y1) * Image.Width + X0) * 4 + Channel]),
                float(Image.Pixels[(size_t(Y1) * Image.Width + X1) * 4 + Channel]), X - X0);
            Result[Channel] = std::lerp(Top, Bottom, Y - Y0);
        }
        return Result;
    }

    static float Edge(Rml::Vector2f A, Rml::Vector2f B, Rml::Vector2f P)
    {
        return (B.x - A.x) * (P.y - A.y) - (B.y - A.y) * (P.x - A.x);
    }

    static bool Covered(float Value, Rml::Vector2f A, Rml::Vector2f B)
    {
        return Value > 0 || (Value == 0 && (B.y < A.y || (B.y == A.y && B.x > A.x)));
    }

    template<typename Function>
    void Rasterize(const Geometry& Data, Rml::Vector2f Translation, Function Draw)
    {
        const auto Bounds = ClipBounds();
        for (size_t Triangle = 0; Triangle + 2 < Data.Indices.size(); Triangle += 3)
        {
            std::array<Rml::Vertex, 3> Vertices;
            std::array<Rml::Vector2f, 3> Points;
            bool Valid = true;
            for (int Index = 0; Index < 3; ++Index)
            {
                const auto VertexIndex = Data.Indices[Triangle + Index];
                if (VertexIndex < 0 || size_t(VertexIndex) >= Data.Vertices.size())
                {
                    Valid = false;
                    break;
                }
                Vertices[Index] = Data.Vertices[VertexIndex];
                const auto Point = Vertices[Index].position + Translation;
                const auto Projected = Transform * Rml::Vector4f(Point.x, Point.y, 0, 1);
                Valid &= std::isfinite(Projected.w) && Projected.w > 0;
                Points[Index] = Rml::Vector2f(Projected.x, Projected.y) / Projected.w;
                Valid &= std::isfinite(Points[Index].x) && std::isfinite(Points[Index].y);
            }
            if (!Valid)
            {
                continue;
            }
            float Area = Edge(Points[0], Points[1], Points[2]);
            if (std::abs(Area) < 1e-6f)
            {
                continue;
            }
            if (Area < 0)
            {
                std::swap(Points[1], Points[2]);
                std::swap(Vertices[1], Vertices[2]);
                Area = -Area;
            }
            const int Left = int((std::max)(float(Bounds.Left()), std::floor((std::min)({Points[0].x, Points[1].x, Points[2].x}))));
            const int Right = int((std::min)(float(Bounds.Right()), std::ceil((std::max)({Points[0].x, Points[1].x, Points[2].x}))));
            const int Top = int((std::max)(float(Bounds.Top()), std::floor((std::min)({Points[0].y, Points[1].y, Points[2].y}))));
            const int Bottom = int((std::min)(float(Bounds.Bottom()), std::ceil((std::max)({Points[0].y, Points[1].y, Points[2].y}))));
            for (int Y = Top; Y < Bottom; ++Y)
            {
                for (int X = Left; X < Right; ++X)
                {
                    const Rml::Vector2f P(X + 0.5f, Y + 0.5f);
                    const float W0 = Edge(Points[1], Points[2], P);
                    const float W1 = Edge(Points[2], Points[0], P);
                    const float W2 = Edge(Points[0], Points[1], P);
                    if (Covered(W0, Points[1], Points[2]) && Covered(W1, Points[2], Points[0]) && Covered(W2, Points[0], Points[1]))
                    {
                        Draw(size_t(Y) * Backend.Width + X, Vertices[0], Vertices[1], Vertices[2], W0 / Area, W1 / Area, W2 / Area);
                    }
                }
            }
        }
    }

    void ApplyFilter(std::vector<uint8_t>& Pixels, const Filter& Data)
    {
        if (Data.Name == "opacity")
        {
            for (auto& Value : Pixels)
            {
                Value = Byte(Value * std::clamp(Data.Value, 0.0f, 1.0f));
            }
            return;
        }
        const float Sigma = std::clamp(Data.Value, 0.0f, 32.0f);
        if (Sigma < 0.01f)
        {
            return;
        }
        const int Radius = int(std::ceil(3 * Sigma));
        std::vector<float> Weights(Radius * 2 + 1);
        float Total = 0;
        for (int Index = -Radius; Index <= Radius; ++Index)
        {
            Weights[Index + Radius] = std::exp(-float(Index * Index) / (2 * Sigma * Sigma));
            Total += Weights[Index + Radius];
        }
        for (auto& Weight : Weights)
        {
            Weight /= Total;
        }
        auto Temporary = Pixels;
        for (int Axis = 0; Axis < 2; ++Axis)
        {
            const auto& Source = Axis == 0 ? Pixels : Temporary;
            auto& Target = Axis == 0 ? Temporary : Pixels;
            const auto Bounds = ClipBounds();
            for (int Y = Bounds.Top(); Y < Bounds.Bottom(); ++Y)
            {
                for (int X = Bounds.Left(); X < Bounds.Right(); ++X)
                {
                    for (int Channel = 0; Channel < 4; ++Channel)
                    {
                        float Sum = 0;
                        for (int Index = -Radius; Index <= Radius; ++Index)
                        {
                            const int SX = X + (Axis == 0 ? Index : 0);
                            const int SY = Y + (Axis == 1 ? Index : 0);
                            if (SX >= Bounds.Left() && SX < Bounds.Right() && SY >= Bounds.Top() && SY < Bounds.Bottom())
                            {
                                Sum += Source[(size_t(SY) * Backend.Width + SX) * 4 + Channel] * Weights[Index + Radius];
                            }
                        }
                        Target[(size_t(Y) * Backend.Width + X) * 4 + Channel] = Byte(Sum);
                    }
                }
            }
        }
    }
};
