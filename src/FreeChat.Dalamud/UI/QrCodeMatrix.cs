using QRCoder;

namespace FreeChat.Dalamud.UI;

public sealed class QrCodeMatrix
{
    private QrCodeMatrix(bool[,] modules)
    {
        this.Modules = modules;
        this.Size = modules.GetLength(0);
    }

    public bool[,] Modules { get; }
    public int Size { get; }

    public static QrCodeMatrix Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var size = rows.Count;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
                modules[y, x] = rows[y][x];
        }

        return new QrCodeMatrix(modules);
    }

    public static byte[] CreatePng(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
