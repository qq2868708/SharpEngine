#include "RenderBackend_Offscreen.h"

#include "../../Logging/EngineLog.h"
#include "../Public/RenderCore.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <limits>

namespace
{
#pragma pack(push, 1)
    struct BmpFileHeader
    {
        uint16_t Type = 0x4d42;
        uint32_t Size = 0;
        uint16_t Reserved1 = 0;
        uint16_t Reserved2 = 0;
        uint32_t OffBits = 0;
    };

    struct BmpInfoHeader
    {
        uint32_t Size = sizeof(BmpInfoHeader);
        int32_t Width = 0;
        int32_t Height = 0;
        uint16_t Planes = 1;
        uint16_t BitCount = 32;
        uint32_t Compression = 0;
        uint32_t ImageSize = 0;
        int32_t XPelsPerMeter = 0;
        int32_t YPelsPerMeter = 0;
        uint32_t ColorsUsed = 0;
        uint32_t ColorsImportant = 0;
    };
#pragma pack(pop)

    bool WriteBmp(const std::filesystem::path& path, uint32_t width,
                  uint32_t height, const std::vector<uint8_t>& rgba)
    {
        if (width == 0 || height == 0 || rgba.size() !=
            static_cast<size_t>(width) * height * 4)
            return false;

        std::error_code error;
        if (!path.parent_path().empty())
            std::filesystem::create_directories(path.parent_path(), error);
        std::ofstream output(path, std::ios::binary);
        if (!output) return false;

        const uint32_t imageSize = width * height * 4;
        BmpFileHeader file{};
        file.OffBits = sizeof(BmpFileHeader) + sizeof(BmpInfoHeader);
        file.Size = file.OffBits + imageSize;
        BmpInfoHeader info{};
        info.Width = static_cast<int32_t>(width);
        info.Height = static_cast<int32_t>(height);
        info.ImageSize = imageSize;
        output.write(reinterpret_cast<const char*>(&file), sizeof(file));
        output.write(reinterpret_cast<const char*>(&info), sizeof(info));

        // The scene vertices use a top-left origin. A positive BMP height is
        // bottom-up, so write rows in reverse and convert RGBA to BGRA.
        std::vector<uint8_t> row(static_cast<size_t>(width) * 4);
        for (uint32_t y = height; y-- > 0;)
        {
            for (uint32_t x = 0; x < width; ++x)
            {
                const auto source = (static_cast<size_t>(y) * width + x) * 4;
                const auto target = static_cast<size_t>(x) * 4;
                row[target + 0] = rgba[source + 2];
                row[target + 1] = rgba[source + 1];
                row[target + 2] = rgba[source + 0];
                row[target + 3] = rgba[source + 3];
            }
            output.write(reinterpret_cast<const char*>(row.data()), row.size());
        }
        return output.good();
    }

    float Edge(float ax, float ay, float bx, float by, float px, float py)
    {
        return (px - ax) * (by - ay) - (py - ay) * (bx - ax);
    }

    uint8_t ToByte(float value)
    {
        if (!std::isfinite(value)) value = 0;
        return static_cast<uint8_t>(std::lround(std::clamp(value, 0.0f, 1.0f) * 255.0f));
    }
}

bool RenderBackend_Offscreen::Initialize(const RenderInitDesc* desc)
{
    if (!desc || desc->Width == 0 || desc->Height == 0)
    {
        EngineLog::Error("RenderCore", "RenderOffscreen requires a non-zero framebuffer size");
        return false;
    }
    Resize(desc->Width, desc->Height);
    Initialized = true;
    Clear();
    EngineLog::Info("RenderCore", "RenderOffscreen initialized without a native graphics device");
    return true;
}

void RenderBackend_Offscreen::Shutdown()
{
    Pixels.clear();
    Depth.clear();
    Width = 0;
    Height = 0;
    Stats = {};
    Initialized = false;
}

void RenderBackend_Offscreen::BeginFrame()
{
    if (!Initialized) return;
    ++Stats.FramesBegun;
    Clear();
}

void RenderBackend_Offscreen::SubmitFrame(const RenderFrame* frame)
{
    if (!Initialized || !frame || frame->ViewportWidth <= 0 ||
        frame->ViewportHeight <= 0)
        return;

    ++Stats.FramesSubmitted;
    Stats.TriangleVertices += frame->Triangles.VertexCount;
    Stats.LineVertices += frame->Lines.VertexCount;
    for (uint32_t index = 0; index + 2 < frame->Triangles.VertexCount; index += 3)
        DrawTriangle(frame->Triangles.Vertices[index],
                     frame->Triangles.Vertices[index + 1],
                     frame->Triangles.Vertices[index + 2], *frame);
    for (uint32_t index = 0; index + 1 < frame->Lines.VertexCount; index += 2)
        DrawLine(frame->Lines.Vertices[index], frame->Lines.Vertices[index + 1], *frame);
}

void RenderBackend_Offscreen::EndFrame()
{
}

void RenderBackend_Offscreen::Resize(uint32_t width, uint32_t height)
{
    Width = width;
    Height = height;
    Pixels.assign(static_cast<size_t>(Width) * Height * 4, 0);
    Depth.assign(static_cast<size_t>(Width) * Height,
                 std::numeric_limits<float>::infinity());
    Stats.Width = Width;
    Stats.Height = Height;
    Stats.HasFramebuffer = Width != 0 && Height != 0;
}

void RenderBackend_Offscreen::Clear()
{
    if (Pixels.empty()) return;
    const auto red = ToByte(0.055f);
    const auto green = ToByte(0.065f);
    const auto blue = ToByte(0.08f);
    for (size_t index = 0; index < Pixels.size(); index += 4)
    {
        Pixels[index + 0] = red;
        Pixels[index + 1] = green;
        Pixels[index + 2] = blue;
        Pixels[index + 3] = 255;
    }
    std::fill(Depth.begin(), Depth.end(), std::numeric_limits<float>::infinity());
}

void RenderBackend_Offscreen::PutPixel(int x, int y, float depth, float r,
                                       float g, float b, float a)
{
    if (x < 0 || y < 0 || x >= static_cast<int>(Width) ||
        y >= static_cast<int>(Height) || !std::isfinite(depth))
        return;
    const auto index = static_cast<size_t>(y) * Width + static_cast<size_t>(x);
    if (depth > Depth[index]) return;
    Depth[index] = depth;
    const auto pixel = index * 4;
    const float alpha = std::clamp(std::isfinite(a) ? a : 1.0f, 0.0f, 1.0f);
    const float inverse = 1.0f - alpha;
    Pixels[pixel + 0] = ToByte(r * alpha + Pixels[pixel + 0] / 255.0f * inverse);
    Pixels[pixel + 1] = ToByte(g * alpha + Pixels[pixel + 1] / 255.0f * inverse);
    Pixels[pixel + 2] = ToByte(b * alpha + Pixels[pixel + 2] / 255.0f * inverse);
    Pixels[pixel + 3] = 255;
}

void RenderBackend_Offscreen::DrawTriangle(const RenderVertex& first,
                                           const RenderVertex& second,
                                           const RenderVertex& third,
                                           const RenderFrame& frame)
{
    const float x0 = frame.ViewportX + first.X;
    const float y0 = frame.ViewportY + first.Y;
    const float x1 = frame.ViewportX + second.X;
    const float y1 = frame.ViewportY + second.Y;
    const float x2 = frame.ViewportX + third.X;
    const float y2 = frame.ViewportY + third.Y;
    const float area = Edge(x0, y0, x1, y1, x2, y2);
    if (std::abs(area) < 1e-6f) return;
    const int minX = std::max(0, static_cast<int>(std::floor(std::min({x0, x1, x2}))));
    const int maxX = std::min(static_cast<int>(Width) - 1,
        static_cast<int>(std::ceil(std::max({x0, x1, x2}))));
    const int minY = std::max(0, static_cast<int>(std::floor(std::min({y0, y1, y2}))));
    const int maxY = std::min(static_cast<int>(Height) - 1,
        static_cast<int>(std::ceil(std::max({y0, y1, y2}))));
    if (minX > maxX || minY > maxY) return;

    for (int y = minY; y <= maxY; ++y)
        for (int x = minX; x <= maxX; ++x)
        {
            const float px = x + 0.5f;
            const float py = y + 0.5f;
            float w0 = Edge(x1, y1, x2, y2, px, py);
            float w1 = Edge(x2, y2, x0, y0, px, py);
            float w2 = Edge(x0, y0, x1, y1, px, py);
            if (area < 0) { w0 = -w0; w1 = -w1; w2 = -w2; }
            if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
            w0 /= std::abs(area); w1 /= std::abs(area); w2 /= std::abs(area);
            PutPixel(x, y, w0 * first.Z + w1 * second.Z + w2 * third.Z,
                     w0 * first.R + w1 * second.R + w2 * third.R,
                     w0 * first.G + w1 * second.G + w2 * third.G,
                     w0 * first.B + w1 * second.B + w2 * third.B,
                     w0 * first.A + w1 * second.A + w2 * third.A);
        }
}

void RenderBackend_Offscreen::DrawLine(const RenderVertex& first,
                                       const RenderVertex& second,
                                       const RenderFrame& frame)
{
    const float x0 = frame.ViewportX + first.X;
    const float y0 = frame.ViewportY + first.Y;
    const float x1 = frame.ViewportX + second.X;
    const float y1 = frame.ViewportY + second.Y;
    const float distance = std::max(std::abs(x1 - x0), std::abs(y1 - y0));
    if (distance < 1e-6f) return;
    const int steps = std::max(1, static_cast<int>(std::ceil(distance)));
    for (int step = 0; step <= steps; ++step)
    {
        const float t = static_cast<float>(step) / steps;
        PutPixel(static_cast<int>(std::lround(x0 + (x1 - x0) * t)),
                 static_cast<int>(std::lround(y0 + (y1 - y0) * t)),
                 first.Z + (second.Z - first.Z) * t,
                 first.R + (second.R - first.R) * t,
                 first.G + (second.G - first.G) * t,
                 first.B + (second.B - first.B) * t,
                 first.A + (second.A - first.A) * t);
    }
}

void RenderBackend_Offscreen::RequestScreenshot(const wchar_t* path)
{
    if (!Initialized || !path || !*path) return;
    const auto output = std::filesystem::path(path);
    if (!WriteBmp(output, Width, Height, Pixels))
    {
        EngineLog::Error("RenderCore", "RenderOffscreen failed to write " + output.string());
        return;
    }
    EngineLog::Info("RenderCore", "RenderOffscreen wrote " + output.string());
}
