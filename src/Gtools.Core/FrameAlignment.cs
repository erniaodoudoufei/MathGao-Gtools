namespace Gtools.Core;

/// <summary>
/// 一帧屏幕内容的逐行特征。只保留每行的亮度均值和水平边缘能量，
/// 因此比较两帧时对动态表情、闪烁光标这类局部像素抖动不敏感，
/// 但对整体上下滚动非常敏感 —— 正好用来实测滚轮到底滚了多少像素。
/// </summary>
public sealed class FrameSignature
{
    public int Width{get;}
    public int Height{get;}
    public float[] Rows{get;}
    public float[] Edges{get;}
    public double Variance{get;}

    private FrameSignature(int width,int height,float[] rows,float[] edges,double variance)
    { Width=width;Height=height;Rows=rows;Edges=edges;Variance=variance; }

    /// <summary>从 8bpp 灰度缓冲构建特征。左右按比例裁掉边缘，避开滚动条和窗口描边。</summary>
    public static FrameSignature FromGray(byte[] gray,int width,int height,int stride,double edgeExclusionRatio=.06)
    {
        if(width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width));
        var margin=Math.Min(width/4,Math.Max(4,(int)(width*Math.Clamp(edgeExclusionRatio,0,.2))));
        var from=margin;var to=width-margin;if(to-from<8){from=0;to=width;}
        var step=Math.Max(1,(to-from)/256);
        var rows=new float[height];var edges=new float[height];
        for(var y=0;y<height;y++)
        {
            var baseIndex=y*stride;double sum=0,edge=0;var count=0;byte previous=0;var hasPrevious=false;
            for(var x=from;x<to;x+=step)
            {
                var v=gray[baseIndex+x];sum+=v;count++;
                if(hasPrevious)edge+=Math.Abs(v-previous);
                previous=v;hasPrevious=true;
            }
            if(count==0)count=1;
            rows[y]=(float)(sum/count);edges[y]=(float)(edge/count);
        }
        double mean=0;for(var y=0;y<height;y++)mean+=rows[y];mean/=height;
        double variance=0;for(var y=0;y<height;y++){var d=rows[y]-mean;variance+=d*d;}
        return new(width,height,rows,edges,variance/height);
    }

    public static byte[] ToGray(byte[] bgra,int width,int height,int stride,byte[]? destination=null)
    {
        var gray=destination is not null&&destination.Length>=width*height?destination:new byte[width*height];
        for(var y=0;y<height;y++)
        {
            var src=y*stride;var dst=y*width;
            for(var x=0;x<width;x++)
            {
                var i=src+x*4;
                gray[dst+x]=(byte)((bgra[i+2]*77+bgra[i+1]*150+bgra[i]*29)>>8);
            }
        }
        return gray;
    }
}

public readonly record struct ShiftEstimate(int Shift,double Confidence,bool IsReliable);

public static class FrameAlignment
{
    /// <summary>
    /// 估计 <paramref name="after"/> 相对 <paramref name="before"/> 的整体竖直位移。
    /// 返回值为正表示内容整体下移（页面向上滚动），为负表示内容上移。
    /// </summary>
    public static ShiftEstimate EstimateShift(FrameSignature before,FrameSignature after,int maxShift=0,double minOverlapRatio=.25)
    {
        if(before.Height!=after.Height||before.Width!=after.Width)return new(0,0,false);
        var height=before.Height;
        var limit=maxShift>0?Math.Min(maxShift,height-1):(int)(height*(1-minOverlapRatio));
        if(limit<1)return new(0,0,false);
        var minOverlap=Math.Max(16,(int)(height*minOverlapRatio));

        var best=int.MinValue;var bestCost=double.MaxValue;var costs=new List<double>(limit*2+1);
        for(var shift=-limit;shift<=limit;shift++)
        {
            var start=Math.Max(0,-shift);var end=Math.Min(height,height-shift);
            var overlap=end-start;if(overlap<minOverlap)continue;
            double cost=0;
            for(var y=start;y<end;y++)
            {
                cost+=Math.Abs(before.Rows[y]-after.Rows[y+shift]);
                cost+=Math.Abs(before.Edges[y]-after.Edges[y+shift]);
            }
            cost/=overlap;
            // 重叠越少越容易“碰巧”对上，轻微惩罚以免偏向极端位移。
            cost*=1+.35*(1-overlap/(double)height);
            costs.Add(cost);
            if(cost<bestCost){bestCost=cost;best=shift;}
        }
        if(best==int.MinValue||costs.Count==0)return new(0,0,false);

        costs.Sort();var median=costs[costs.Count/2];
        var confidence=median<=1e-6?(bestCost<=1e-6?1:0):Math.Clamp(1-bestCost/median,0,1);
        // 内容几乎是纯色时逐行特征无意义，直接判定不可信。
        var reliable=before.Variance>4&&after.Variance>4&&confidence>=.25;
        return new(best,confidence,reliable);
    }
}
