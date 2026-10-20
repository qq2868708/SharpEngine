"""Render the actual saved Blender dependency-graph geometry to portable previews.

Run using Python with numpy and Pillow after ValidateUEFNMannequinSource.py.
The preview camera follows horizontal root motion; the stage grid stays fixed.
"""
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

Root = Path(__file__).resolve().parents[2]
Output = Root / 'Resources/SourceAssets/UEFNMannequin/Previews'
Output.mkdir(parents=True, exist_ok=True)
Data = np.load(Root / 'Saved/Validation/UEFNMannequinAuthoring-20261020/evaluated-geometry.npz')
FontPath = 'C:/Windows/Fonts/msyh.ttc'
Font = ImageFont.truetype(FontPath, 18)
Small = ImageFont.truetype(FontPath, 13)
Title = ImageFont.truetype(FontPath, 24)
Labels = {'Idle': '站立待机', 'Walk': '行走', 'Run': '跑步', 'Jump': '跳跃'}
Triangles = Data['triangles']
Angle = np.radians(27)
Elevation = np.radians(9)
Right = np.array([np.cos(Angle), np.sin(Angle), 0])
Depth = np.array([np.sin(Angle) * np.cos(Elevation), -np.cos(Angle) * np.cos(Elevation), np.sin(Elevation)])
Up = np.cross(Depth, Right)
Light = np.array([-0.5, -0.7, 1.0])
Light /= np.linalg.norm(Light)
Width = 300
Height = 480
Scale = 185

def Project(Points, RootPosition):
    Centred = Points.copy()
    Centred[:, :2] -= RootPosition[:2]
    return np.column_stack((Width / 2 + Centred @ Right * Scale, 422 - Centred @ Up * Scale))

def Render(Name, Frame):
    Vertices = Data[Name + '_positions'][Frame]
    RootPosition = Data[Name + '_roots'][Frame]
    Canvas = Image.new('RGB', (Width, Height), '#17212d')
    Draw = ImageDraw.Draw(Canvas)
    for X in np.arange(-2, 2.01, 0.5):
        Line = Project(np.array([[X, -5, -0.001], [X, 3, -0.001]]), RootPosition)
        Draw.line([tuple(Point) for Point in Line], fill='#283748')
    for Y in np.arange(-5, 3.01, 0.5):
        Line = Project(np.array([[-2, Y, -0.001], [2, Y, -0.001]]), RootPosition)
        Draw.line([tuple(Point) for Point in Line], fill='#283748')
    Points = Project(Vertices, RootPosition)
    Faces = Vertices[Triangles]
    Normals = np.cross(Faces[:, 1] - Faces[:, 0], Faces[:, 2] - Faces[:, 0])
    Normals /= np.maximum(np.linalg.norm(Normals, axis=1)[:, None], 1e-12)
    Brightness = 0.40 + 0.60 * np.maximum(0, Normals @ Light)
    FaceDepths = np.mean(Faces @ Depth, axis=1)
    Colors = np.clip(Brightness[:, None] * np.array([179, 200, 215]), 0, 255).astype(np.uint8)
    for Index in np.argsort(FaceDepths):
        Draw.polygon([tuple(Point) for Point in Points[Triangles[Index]]], fill=tuple(Colors[Index]))
    Draw.rectangle((0, 0, Width, 63), fill='#17212d')
    Draw.text((16, 10), Name + '  ' + Labels[Name], font=Font, fill='#edf3fc')
    Draw.text((16, 39), f'Frame {Frame + 1:02d}  /  {(len(Data[Name + "_roots"])):02d}    30 fps', font=Small, fill='#94a8be')
    Draw.rectangle((0, 447, Width, Height), fill='#17212d')
    Draw.text((12, 453), f'root Y {RootPosition[1]:+.2f} m    Z {RootPosition[2]:+.2f} m', font=Small, fill='#8fdac4')
    return Canvas

ContactSheet = Image.new('RGB', (Width * 4, 60 + Height * 3), '#111a25')
Draw = ImageDraw.Draw(ContactSheet)
Draw.text((20, 14), 'Blender 源文件实际求值预览 · 相机跟随水平根运动', font=Title, fill='#ffffff')
SampleIndices = {'Idle': [0, 15, 45], 'Walk': [0, 8, 16], 'Run': [0, 5, 10], 'Jump': [7, 20, 30]}
for Column, Name in enumerate(Labels):
    Count = len(Data[Name + '_roots'])
    End = Count if Name == 'Jump' else Count - 1
    Frames = [Render(Name, Index) for Index in range(End)]
    Durations = [30 if Index % 3 != 2 else 40 for Index in range(End)]
    if Name == 'Jump':
        Durations[-1] = 500
    Frames[0].save(Output / (Name + '.gif'), save_all=True, append_images=Frames[1:],
        duration=Durations, loop=0, optimize=False, disposal=2)
    for Row, Index in enumerate(SampleIndices[Name]):
        ContactSheet.paste(Render(Name, Index), (Column * Width, 60 + Row * Height))
ContactSheet.save(Output / 'Actions.png')
print(Output / 'Actions.png')
