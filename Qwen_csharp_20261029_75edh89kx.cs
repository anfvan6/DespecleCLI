using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

class Despeckle
{
    static int Main(string[] args)
    {
        var positional = new List<string>();
        bool invert = false;

        foreach (var arg in args)
        {
            if (arg == "-i" || arg == "--invert")
            {
                invert = true;
            }
            else if (arg == "-h" || arg == "--help")
            {
                PrintUsage();
                return 0;
            }
            else
            {
                positional.Add(arg);
            }
        }

        if (positional.Count < 3)
        {
            PrintUsage();
            return 1;
        }

        string input = positional[0];
        string output = positional[1];

        int maxArea;
        if (!int.TryParse(positional[2], out maxArea) || maxArea < 0)
        {
            Console.Error.WriteLine("maxArea должен быть целым числом >= 0");
            return 1;
        }

        int connectivity = 8;
        if (positional.Count > 3)
        {
            if (!int.TryParse(positional[3], out connectivity) || (connectivity != 4 && connectivity != 8))
            {
                Console.Error.WriteLine("connectivity может быть 4 или 8");
                return 1;
            }
        }

        int threshold = 128;
        if (positional.Count > 4)
        {
            if (!int.TryParse(positional[4], out threshold) || threshold < 1 || threshold > 255)
            {
                Console.Error.WriteLine("threshold должен быть в диапазоне 1..255");
                return 1;
            }
        }

        try
        {
            ProcessImage(input, output, maxArea, connectivity, threshold, invert);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 2;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("Удаление мелких чёрных точек из чёрно-белого изображения.");
        Console.WriteLine();
        Console.WriteLine("Использование:");
        Console.WriteLine("  despeckle.exe input output maxArea [connectivity] [threshold] [-i|--invert]");
        Console.WriteLine();
        Console.WriteLine("Параметры:");
        Console.WriteLine("  input        входной файл: png, bmp, tif, jpg и т.п.");
        Console.WriteLine("  output       выходной файл; формат определяется по расширению");
        Console.WriteLine("  maxArea      удалять чёрные связные компоненты с площадью <= maxArea пикселей");
        Console.WriteLine("  connectivity 4 или 8; по умолчанию 8");
        Console.WriteLine("  threshold    порог бинаризации 1..255; по умолчанию 128");
        Console.WriteLine("  -i, --invert инвертировать логику: удалять светлые точки на тёмном фоне,");
        Console.WriteLine("               но вернуть исходную полярность в результате");
        Console.WriteLine();
        Console.WriteLine("Примеры:");
        Console.WriteLine("  despeckle.exe page.png clean.png 20");
        Console.WriteLine("  despeckle.exe page.tif clean.tif 35 8 160");
        Console.WriteLine("  despeckle.exe page.png clean.png 25 8 128 --invert");
    }

    static void ProcessImage(string input, string output, int maxArea, int connectivity, int threshold, bool invert)
    {
        using (Bitmap src = new Bitmap(input))
        {
            int w = src.Width;
            int h = src.Height;

            long pixelCount = (long)w * h;
            if (pixelCount > int.MaxValue)
            {
                throw new InvalidOperationException("Изображение слишком большое для обработки этим инструментом.");
            }

            byte[] mask = LoadBinaryMask(src, w, h, threshold, invert);
            RemoveSmallBlackComponents(mask, w, h, maxArea, connectivity);

            if (invert)
            {
                InvertMask(mask);
            }

            using (Bitmap dst = Create1bppBitmap(mask, w, h))
            {
                try
                {
                    dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
                }
                catch
                {
                    // Если формат или DPI некорректны, просто игнорируем.
                }

                ImageFormat fmt = GetImageFormat(output);

                // JPEG плохо подходит для 1-битной графики.
                // Если очень нужно, сохраняем через 24-битную копию.
                if (fmt == ImageFormat.Jpeg)
                {
                    using (Bitmap rgb = new Bitmap(dst.Width, dst.Height, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(rgb))
                        {
                            g.Clear(Color.White);
                            g.DrawImage(dst, 0, 0, dst.Width, dst.Height);
                        }

                        try
                        {
                            rgb.SetResolution(dst.HorizontalResolution, dst.VerticalResolution);
                        }
                        catch
                        {
                        }

                        rgb.Save(output, fmt);
                    }
                }
                else
                {
                    dst.Save(output, fmt);
                }
            }
        }
    }

    static byte[] LoadBinaryMask(Bitmap src, int w, int h, int threshold, bool invert)
    {
        byte[] mask = new byte[w * h];

        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.DrawImage(src, 0, 0, w, h);
            }

            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < h; y++)
                {
                    byte* p = basePtr + y * stride;
                    int rowStart = y * w;

                    for (int x = 0; x < w; x++, p += 4)
                    {
                        int blue = p[0];
                        int green = p[1];
                        int red = p[2];

                        // ITU-R BT.601 luma, целочисленно:
                        // (R * 77 + G * 150 + B * 29) / 256
                        int luminance = (red * 77 + green * 150 + blue * 29) >> 8;

                        bool isObject;
                        if (invert)
                        {
                            // Светлые объекты считаются "чёрными" для удаления.
                            isObject = luminance >= threshold;
                        }
                        else
                        {
                            // Обычный случай: чёрные точки на белом фоне.
                            isObject = luminance < threshold;
                        }

                        mask[rowStart + x] = isObject ? (byte)1 : (byte)0;
                    }
                }
            }

            bmp.UnlockBits(data);
        }

        return mask;
    }

    static void RemoveSmallBlackComponents(byte[] mask, int w, int h, int maxArea, int connectivity)
    {
        if (maxArea <= 0)
        {
            return;
        }

        int total = w * h;

        // Очередь для обхода связной компоненты.
        // Сначала выделяем разумный кусок, при необходимости увеличим.
        int[] queue = new int[Math.Min(total, 1 << 20)];

        var componentPixels = new List<int>();

        int[] dx;
        int[] dy;

        if (connectivity == 4)
        {
            dx = new int[] { 1, -1, 0, 0 };
            dy = new int[] { 0, 0, 1, -1 };
        }
        else
        {
            dx = new int[] { -1, -1, -1, 0, 0, 1, 1, 1 };
            dy = new int[] { -1, 0, 1, -1, 1, -1, 0, 1 };
        }

        for (int i = 0; i < total; i++)
        {
            if (mask[i] != 1)
            {
                continue;
            }

            int head = 0;
            int tail = 0;

            mask[i] = 2; // 2 = уже посещённый чёрный пиксель
            queue[tail++] = i;

            int area = 0;
            bool storePixels = true;
            componentPixels.Clear();

            while (head < tail)
            {
                int idx = queue[head++];
                area++;

                if (storePixels)
                {
                    componentPixels.Add(idx);

                    // Если компонента уже больше maxArea, дальше хранить её пиксели не нужно.
                    if (componentPixels.Count > maxArea)
                    {
                        storePixels = false;
                        componentPixels.Clear();
                    }
                }

                int x = idx % w;
                int y = idx / w;

                for (int k = 0; k < dx.Length; k++)
                {
                    int nx = x + dx[k];
                    int ny = y + dy[k];

                    if ((uint)nx < (uint)w && (uint)ny < (uint)h)
                    {
                        int ni = ny * w + nx;

                        if (mask[ni] == 1)
                        {
                            mask[ni] = 2;

                            if (tail == queue.Length)
                            {
                                long doubled = (long)queue.Length * 2;
                                int newLength = (int)Math.Min((long)total, doubled);

                                if (newLength <= queue.Length)
                                {
                                    newLength = total;
                                }

                                Array.Resize(ref queue, newLength);
                            }

                            queue[tail++] = ni;
                        }
                    }
                }
            }

            // Если компонента маленькая, удаляем её.
            if (area <= maxArea)
            {
                for (int j = 0; j < componentPixels.Count; j++)
                {
                    mask[componentPixels[j]] = 0;
                }
            }
        }

        // Возвращаем посещённым чёрным пикселям состояние 1.
        for (int i = 0; i < total; i++)
        {
            if (mask[i] == 2)
            {
                mask[i] = 1;
            }
        }
    }

    static void InvertMask(byte[] mask)
    {
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = (byte)(mask[i] == 0 ? 1 : 0);
        }
    }

    static Bitmap Create1bppBitmap(byte[] mask, int w, int h)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format1bppIndexed);

        ColorPalette palette = bmp.Palette;
        if (palette.Entries.Length >= 2)
        {
            palette.Entries[0] = Color.White;
            palette.Entries[1] = Color.Black;
            bmp.Palette = palette;
        }

        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);

        unsafe
        {
            byte* basePtr = (byte*)data.Scan0;
            int stride = data.Stride;

            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;

                // Обнуляем строку вместе с padding-байтами.
                // В нашей палитре 0 = белый.
                for (int i = 0; i < stride; i++)
                {
                    row[i] = 0;
                }

                int rowStart = y * w;

                for (int x = 0; x < w; x++)
                {
                    if (mask[rowStart + x] == 1)
                    {
                        // В 1bpp DIB/GDI+ левый пиксель — старший бит.
                        row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }
                }
            }
        }

        bmp.UnlockBits(data);
        return bmp;
    }

    static ImageFormat GetImageFormat(string path)
    {
        string ext = Path.GetExtension(path);

        if (string.IsNullOrEmpty(ext))
        {
            return ImageFormat.Png;
        }

        switch (ext.ToLowerInvariant())
        {
            case ".bmp":
                return ImageFormat.Bmp;

            case ".jpg":
            case ".jpeg":
                return ImageFormat.Jpeg;

            case ".tif":
            case ".tiff":
                return ImageFormat.Tiff;

            case ".gif":
                return ImageFormat.Gif;

            default:
                return ImageFormat.Png;
        }
    }
}