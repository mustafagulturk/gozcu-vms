using Gozcu.Vision;

namespace Gozcu.UnitTests.Vision;

public class FrameTensorTests
{
    [Fact]
    public void Interleaved_bgr_becomes_planar_chw_keeping_bgr_order_and_raw_values()
    {
        // 2x2 kare, satır satır: (B,G,R) pikselleri.
        byte[] frame =
        [
            1, 2, 3, 4, 5, 6,
            7, 8, 9, 250, 251, 255,
        ];
        var tensor = new float[FrameTensor.Length(2, 2)];

        FrameTensor.FillChw(frame, 2, 2, tensor);

        Assert.Equal(
            [
                1, 4, 7, 250,    // B düzlemi (kanal 0)
                2, 5, 8, 251,    // G düzlemi (kanal 1)
                3, 6, 9, 255,    // R düzlemi (kanal 2), 255'e bölünmez
            ],
            tensor);
    }

    [Fact]
    public void Non_square_frame_is_laid_out_row_major()
    {
        // 3x1 kare: piksel x'in mavi değeri x.
        byte[] frame = [0, 10, 20, 1, 11, 21, 2, 12, 22];
        var tensor = new float[FrameTensor.Length(3, 1)];

        FrameTensor.FillChw(frame, 3, 1, tensor);

        Assert.Equal([0f, 1f, 2f], tensor[..3]);
        Assert.Equal([20f, 21f, 22f], tensor[6..]);
    }

    [Fact]
    public void Larger_pooled_destination_is_accepted()
    {
        var tensor = new float[100];

        FrameTensor.FillChw(new byte[12], 2, 2, tensor);
    }

    [Fact]
    public void Frame_of_wrong_size_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() => FrameTensor.FillChw(new byte[11], 2, 2, new float[12]));
        Assert.Equal("bgrFrame", error.ParamName);
    }

    [Fact]
    public void Too_small_destination_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() => FrameTensor.FillChw(new byte[12], 2, 2, new float[11]));
        Assert.Equal("destination", error.ParamName);
    }
}
