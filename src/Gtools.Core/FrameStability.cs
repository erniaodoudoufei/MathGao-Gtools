namespace Gtools.Core;

/// <summary>Spatial samples retain local repainting; zero estimated scroll is not stability.</summary>
public sealed record StabilitySample(int Width,int Height,byte[] Pixels)
{
    public static byte[] ToGray(byte[] bgra,int width,int height,int stride)
    {
        var gray=new byte[width*height];
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var i=y*stride+x*4;gray[y*width+x]=(byte)((bgra[i+2]*77+bgra[i+1]*150+bgra[i]*29)>>8);
        }
        return gray;
    }
    public static StabilitySample FromGray(byte[] gray,int width,int height,int stride)
    {
        var w=Math.Min(width,320);var h=Math.Min(height,320);var pixels=new byte[w*h];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)pixels[y*w+x]=gray[(y*height/h)*stride+x*width/w];
        return new(w,h,pixels);
    }
    public bool IsSimilarTo(StabilitySample other)
    {
        if(Width!=other.Width||Height!=other.Height)return false;
        double delta=0,edgeDelta=0;var changed=0;
        for(var by=0;by<Height;by+=16)for(var bx=0;bx<Width;bx+=16)
        {
            var local=0;var count=0;
            for(var y=by;y<Math.Min(Height,by+16);y++)for(var x=bx;x<Math.Min(Width,bx+16);x++)
            {
                var i=y*Width+x;var d=Math.Abs(Pixels[i]-other.Pixels[i]);delta+=d;count++;
                if(d>8){changed++;local++;}
                if(y>0)edgeDelta+=Math.Abs((Pixels[i]-Pixels[i-Width])-(other.Pixels[i]-other.Pixels[i-Width]));
            }
            if(local>count*.10)return false;
        }
        return delta/Pixels.Length<=.35&&edgeDelta/Pixels.Length<=.55&&changed<=Pixels.Length*.002;
    }
}

public sealed class FrameStabilityTracker
{
    private StabilitySample? _last;
    private long _stableSince;
    public bool Observe(StabilitySample sample,long milliseconds)
    {
        if(_last is null||!_last.IsSimilarTo(sample))_stableSince=milliseconds;
        _last=sample;
        return milliseconds-_stableSince>=300;
    }
}
