using System.Text;
using System.Text.RegularExpressions;

namespace Gtools.Core;

/// <summary>
/// OCR 按屏幕上的视觉行返回文字，一句话折成两行就成了两条。逐行送去翻译会把句子拆散
/// （“…to leave / him alone” 被译成“离开 / 他一个人”），这里按版面把折行还原成段落。
/// 只有几何位置和文字线索都支持时才合并；拿不准就保留原来的换行，最坏情况等同于不处理。
/// </summary>
public static partial class OcrTextLayout
{
    // 以下阈值按真实 PaddleOCR 检测框标定（12–24px 屏幕文字）：
    // 同段相邻行的框间隙为 0–0.54 倍行高，段落之间为 0.70–1.06 倍，两者只隔一条窄缝，
    // 所以三行以上改用相对行距判断：段间行距是段内行距的 1.55 倍左右。
    private const double SameRowCenterTolerance=.5;
    private const double RowFragmentGap=1.2;
    private const double AlignedLeftTolerance=1.0;
    private const double CenteredTolerance=.6;
    private const double FirstLineIndentLimit=3.2;
    private const double MaxBlockGap=1.3;
    private const double TwoLineGap=.65;
    private const double ParagraphPitchRatio=1.38;
    // 菜单、标签这类各自独立的短行实测宽度不到 5 倍行高；被折行的句子都在 9 倍以上。
    private const double MinWrappedLineWidth=5;

    public static string Reflow(IReadOnlyList<OcrRegion>? regions,string fallback)
    {
        if(regions is null||regions.Count<2)return fallback;
        var lines=new List<Line>();
        foreach(var region in regions)
        {
            var text=region.Text?.Trim()??"";
            if(text.Length==0)continue;
            // 缺少坐标就无法判断版面，整段退回 OCR 原文，不做半截处理。
            if(region.Polygon is not{Length:>=3} polygon)return fallback;
            var line=new Line(polygon.Min(p=>p.X),polygon.Max(p=>p.X),polygon.Min(p=>p.Y),polygon.Max(p=>p.Y),text);
            if(!(line.H>0)||!(line.W>0))return fallback;
            lines.Add(line);
        }
        if(lines.Count<2)return fallback;
        var paragraphs=new List<string>();
        foreach(var block in SplitBlocks(MergeRowFragments(lines)))AppendParagraphs(paragraphs,block);
        return string.Join(Environment.NewLine,paragraphs);
    }

    /// <summary>检测器偶尔把同一行在宽空格处切成两个框；紧挨着的拼回一行，隔得远的（表格的列）各自成行。</summary>
    private static List<Line> MergeRowFragments(List<Line> lines)
    {
        var rows=new List<List<Line>>();
        foreach(var line in lines.OrderBy(l=>l.Cy))
        {
            var row=rows.Count>0?rows[^1]:null;
            if(row is not null&&line.Cy-row.Average(l=>l.Cy)<=SameRowCenterTolerance*Math.Min(line.H,row.Min(l=>l.H)))row.Add(line);
            else rows.Add(new(){line});
        }
        var result=new List<Line>();
        foreach(var row in rows)
        {
            Line? current=null;
            foreach(var line in row.OrderBy(l=>l.L))
            {
                if(current is not null&&line.L-current.R<=RowFragmentGap*Math.Min(line.H,current.H))
                    current=new(current.L,Math.Max(current.R,line.R),Math.Min(current.T,line.T),Math.Max(current.B,line.B),current.Text+Joiner(current.Text,line.Text)+line.Text);
                else{if(current is not null)result.Add(current);current=line;}
            }
            if(current is not null)result.Add(current);
        }
        return result;
    }

    /// <summary>先按对齐方式和上下相邻关系分成版面块，再按行距把块切成段落候选。</summary>
    private static IEnumerable<Block> SplitBlocks(List<Line> lines)
    {
        var candidates=new List<Block>();
        Block? block=null;
        foreach(var line in lines)
        {
            if(block is null||!block.TryAdd(line)){block=new(line);candidates.Add(block);}
        }
        foreach(var candidate in candidates)
        {
            var items=candidate.Lines;
            if(items.Count<2){yield return candidate;continue;}
            var pitches=new double[items.Count-1];
            for(var i=1;i<items.Count;i++)pitches[i-1]=items[i].Cy-items[i-1].Cy;
            // 取偏小的分位数作为“段内行距”：三行里夹一个段落间隔时，中位数会被间隔拉高。
            var basePitch=pitches.OrderBy(p=>p).ElementAt((int)Math.Floor((pitches.Length-1)*.25));
            var part=new List<Line>{items[0]};
            for(var i=1;i<items.Count;i++)
            {
                var gap=items[i].T-items[i-1].B;var height=Math.Min(items[i].H,items[i-1].H);
                var paragraphBreak=pitches.Length>=2?pitches[i-1]>ParagraphPitchRatio*basePitch:gap>TwoLineGap*height;
                if(paragraphBreak){yield return candidate.With(part);part=new();}
                part.Add(items[i]);
            }
            yield return candidate.With(part);
        }
    }

    private static void AppendParagraphs(List<string> paragraphs,Block block)
    {
        var lines=block.Lines;
        var cjk=IsMostlyCjk(lines);
        // 西文块里只要有一行以小写开头，就说明这是成段的句子而不是标题列表。
        var sentenceEvidence=lines.Skip(1).Any(l=>StartOf(l.Text)==StartKind.Lower);
        var paragraph=new StringBuilder(lines[0].Text);
        for(var i=1;i<lines.Count;i++)
        {
            var previous=lines[i-1];var current=lines[i];
            var merge=previous.W>=MinWrappedLineWidth*previous.H&&!EndsSentence(previous.Text)&&!StartsListItem(current.Text)
                &&Continues(block,previous,current,cjk,sentenceEvidence);
            if(merge){paragraph.Append(Joiner(previous.Text,current.Text)).Append(current.Text);continue;}
            paragraphs.Add(paragraph.ToString());paragraph.Clear().Append(current.Text);
        }
        paragraphs.Add(paragraph.ToString());
    }

    private static bool Continues(Block block,Line previous,Line current,bool cjk,bool sentenceEvidence)
    {
        var start=StartOf(current.Text);
        // 居中排版（字幕、海报）没有“写满一行”这回事，只认文字线索。
        if(block.Centered)return cjk?EndsWithComma(previous.Text):start==StartKind.Lower;
        var full=previous.R>=block.Right-Math.Max(2.5*previous.H,.2*(block.Right-block.Left));
        if(cjk)return full;
        // 小写开头几乎一定是上一行的延续；大写或数字开头时，要求上一行写满且本块确实是成段句子。
        return start==StartKind.Lower||(full&&sentenceEvidence);
    }

    private static string Joiner(string previous,string next)
    {
        var last=previous[^1];var first=next[0];
        // 行尾连字符断词：直接接上，保留连字符（无法区分 inter-national 与 well-known，保留更安全）。
        if(last=='-'&&previous.Length>=2&&char.IsLetter(previous[^2])&&char.IsLower(first))return "";
        return IsCjk(last)||IsCjk(first)?"":" ";
    }

    private static bool EndsSentence(string text)
    {
        var trimmed=text.TrimEnd('"','\'','”','’',')','）',']','】','》','」','』',' ');
        return trimmed.Length>0&&".!?。！？…:：;；".Contains(trimmed[^1]);
    }

    private static bool EndsWithComma(string text)=>",，、".Contains(text[^1]);

    private static bool StartsListItem(string text)=>ListItemPattern().IsMatch(text);

    [GeneratedRegex(@"^(?:[-•·*▪◦●○■□➤▶→–—]\s|[（(]?\d{1,3}[.)）、](?!\d)|[（(]?[一二三四五六七八九十]{1,3}[、.)）]|[①-⑳]|[A-Za-z][.)]\s)")]
    private static partial Regex ListItemPattern();

    private static StartKind StartOf(string text)
    {
        foreach(var c in text)
        {
            if("\"'“‘（([【《「『 ".Contains(c))continue;
            return IsCjk(c)?StartKind.Cjk:char.IsLower(c)?StartKind.Lower:char.IsUpper(c)?StartKind.Upper:StartKind.Other;
        }
        return StartKind.Other;
    }

    private static bool IsMostlyCjk(IReadOnlyList<Line> lines)
    {
        int cjk=0,latin=0;
        foreach(var line in lines)foreach(var c in line.Text)
        {
            if(c is >='一' and <='鿿')cjk++;
            else if(c is >='A' and <='Z' or >='a' and <='z')latin++;
        }
        return cjk>=Math.Max(1,latin/3);
    }

    private static bool IsCjk(char c)=>c is >='　' and <='ヿ' or >='㐀' and <='䶿' or >='一' and <='鿿' or >='＀' and <='￯';

    private enum StartKind{Lower,Upper,Cjk,Other}

    private sealed record Line(double L,double R,double T,double B,string Text)
    {
        public double H=>B-T;
        public double W=>R-L;
        public double Cx=>(L+R)/2;
        public double Cy=>(T+B)/2;
    }

    private sealed class Block
    {
        private bool? _centered;
        public List<Line> Lines{get;}=new();
        public double Left{get;private set;}
        public double Right{get;private set;}
        public bool Centered=>_centered==true;
        public Block(Line first){Lines.Add(first);Left=first.L;Right=first.R;}
        private Block(Block source,List<Line> lines){Lines=lines;Left=source.Left;Right=source.Right;_centered=source._centered;}
        /// <summary>段落候选沿用整个版面块的左右边界：最后一段只有两行时，也能判断第一行是否“写满”。</summary>
        public Block With(List<Line> lines)=>new(this,lines);

        public bool TryAdd(Line line)
        {
            var previous=Lines[^1];var height=Math.Min(previous.H,line.H);
            var stacked=line.Cy-previous.Cy>=.6*height&&line.T-previous.B<=MaxBlockGap*height;
            if(!stacked)return false;
            var aligned=Math.Abs(line.L-previous.L)<=AlignedLeftTolerance*height;
            var centered=Math.Abs(line.Cx-previous.Cx)<=CenteredTolerance*height;
            // 中文段首缩进两格：第一行比后面的行靠右，仍属于同一块，左边界以后面的行为准。
            // 居中排版也会让上一行更靠右，但它左右对称地收窄、中线不动；缩进只动左边，中线会偏移，以此区分。
            var indentedFirstLine=!centered&&Lines.Count==1&&previous.L-line.L>AlignedLeftTolerance*height&&previous.L-line.L<=FirstLineIndentLimit*height;
            if(_centered is null)
            {
                if(aligned||indentedFirstLine)_centered=false;
                else if(centered)_centered=true;
                else return false;
            }
            else if(_centered==true?!centered:!aligned)return false;
            Lines.Add(line);Left=Math.Min(Left,line.L);Right=Math.Max(Right,line.R);
            return true;
        }
    }
}
