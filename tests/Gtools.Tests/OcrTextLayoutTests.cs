using Gtools.Core;

namespace Gtools.Tests;

/// <summary>
/// The fixtures marked "real geometry" are the boxes the bundled PaddleOCR worker returned for synthetic screen
/// text rendered at 12–24px; only made-up sentences are used, never captured content.
/// </summary>
public sealed class OcrTextLayoutTests
{
    private static OcrRegion R(double left,double right,double top,double bottom,string text)=>
        new(text,.99f,new OcrPoint[]{new((float)left,(float)top),new((float)right,(float)top),new((float)right,(float)bottom),new((float)left,(float)bottom)});
    private static string Raw(IEnumerable<OcrRegion> regions)=>string.Join(Environment.NewLine,regions.Select(x=>x.Text));
    private static string[] Reflow(OcrRegion[] regions)=>OcrTextLayout.Reflow(regions,Raw(regions)).Split(Environment.NewLine);

    // ---- real geometry ----
    private static readonly OcrRegion[] EnglishParagraphs=
    {
        R(11,285,18,34,"The committee has decided to postpone the"),
        R(12,281,40,53,"meeting until further notice because several"),
        R(12,203,60,73,"members are unable to attend."),
        R(11,303,90,106,"A new date will be announced by email as soon"),
        R(11,294,110,126,"as the venue has been confirmed. Please keep"),
        R(13,162,132,145,"Thursday afternoon free")
    };

    private static readonly OcrRegion[] TightTwoLines=
    {
        R(12,158,15,30,"He tried to get him to leave"),
        R(12,67,30,44,"him alone")
    };

    private static readonly OcrRegion[] CenteredSubtitle=
    {
        R(95,423,18,43,"I never thought it would end like this,"),
        R(190,327,46,66,"but here we are")
    };

    private static readonly OcrRegion[] HeadlineList=
    {
        R(13,215,18,32,"Getting started with the new API"),
        R(12,209,39,53,"Advanced configuration options"),
        R(13,233,58,73,"Troubleshooting common problems"),
        R(12,181,78,93,"Frequently asked questions")
    };

    private static readonly OcrRegion[] Menu=
    {
        R(12,34,16,33,"File"),
        R(11,38,37,55,"Edit"),
        R(12,42,61,77,"View"),
        R(11,62,82,99,"Window"),
        R(11,42,104,124,"Help")
    };

    private static readonly OcrRegion[] ChineseParagraphs=
    {
        R(11,280,16,36,"我们决定把这次会议推迟到下周，因为有好"),
        R(11,266,38,58,"几位成员没办法到场参加；具体时间另行"),
        R(12,202,60,80,"通知，请大家留意群内的消息。"),
        R(12,271,94,114,"另外，上次讨论的预算方案已经通过审批，"),
        R(12,271,116,135,"财务部门会在本周五之前把明细发给各组。")
    };

    private static readonly OcrRegion[] ChineseList=
    {
        R(11,43,16,33,"苹果"),
        R(11,43,38,55,"香蕉"),
        R(11,85,59,77,"橘子和柚子"),
        R(12,43,82,99,"西瓜")
    };

    private static readonly OcrRegion[] HeadingBodyAndCells=
    {
        R(12,161,18,41,"Release notes"),
        R(12,258,59,80,"This update improves stability when the"),
        R(12,261,80,98,"network connection is slow or unreliable."),
        R(11,95,113,132,"Name: Alice"),
        R(134,228,115,133,"Role: Engineer")
    };

    [Fact]
    public void WrappedSentenceBecomesOneLineSoItIsTranslatedAsOneSentence()
    {
        Assert.Equal(new[]{"He tried to get him to leave him alone"},Reflow(TightTwoLines));
    }

    [Fact]
    public void EnglishParagraphsAreRejoinedAndSplitAtTheWiderLineGap()
    {
        Assert.Equal(new[]
        {
            "The committee has decided to postpone the meeting until further notice because several members are unable to attend.",
            // The last line starts with a capital, but the line above is full width and the block clearly holds sentences.
            "A new date will be announced by email as soon as the venue has been confirmed. Please keep Thursday afternoon free"
        },Reflow(EnglishParagraphs));
    }

    [Fact]
    public void ChineseParagraphsJoinWithoutSpacesAndKeepTheParagraphBreak()
    {
        Assert.Equal(new[]
        {
            "我们决定把这次会议推迟到下周，因为有好几位成员没办法到场参加；具体时间另行通知，请大家留意群内的消息。",
            "另外，上次讨论的预算方案已经通过审批，财务部门会在本周五之前把明细发给各组。"
        },Reflow(ChineseParagraphs));
    }

    [Fact]
    public void CenteredSubtitleContinuesOnALowercaseStart()
    {
        Assert.Equal(new[]{"I never thought it would end like this, but here we are"},Reflow(CenteredSubtitle));
    }

    [Fact]
    public void IndependentLinesAreLeftExactlyAsRecognised()
    {
        // Headline lists, menus and short CJK lists are separate items, not wrapped sentences.
        Assert.Equal(Raw(HeadlineList),OcrTextLayout.Reflow(HeadlineList,Raw(HeadlineList)));
        Assert.Equal(Raw(Menu),OcrTextLayout.Reflow(Menu,Raw(Menu)));
        Assert.Equal(Raw(ChineseList),OcrTextLayout.Reflow(ChineseList,Raw(ChineseList)));
    }

    [Fact]
    public void HeadingBodyAndSideBySideCellsStaySeparate()
    {
        Assert.Equal(new[]
        {
            "Release notes",
            "This update improves stability when the network connection is slow or unreliable.",
            "Name: Alice",
            "Role: Engineer"
        },Reflow(HeadingBodyAndCells));
    }

    // ---- constructed geometry for rules the rendered samples do not reach ----

    [Fact]
    public void SentenceEndingPunctuationAndListMarkersStartANewLine()
    {
        var ended=new[]{R(10,300,10,26,"The first sentence fills the whole line."),R(10,220,30,46,"then an unrelated fragment follows")};
        Assert.Equal(2,Reflow(ended).Length);
        var chinese=new[]{R(10,300,10,30,"第一句话正好写满了这一整行的宽度。"),R(10,300,32,52,"第二句话也同样写满了这一整行的宽度")};
        Assert.Equal(2,Reflow(chinese).Length);
        var numbered=new[]{R(10,300,10,26,"1. open the settings window and choose"),R(10,300,30,46,"2. press the save button when you are"),R(10,120,50,66,"done editing")};
        Assert.Equal(new[]{"1. open the settings window and choose","2. press the save button when you are done editing"},Reflow(numbered));
    }

    [Fact]
    public void HyphenatedWordIsJoinedWithoutASpaceAndDecimalsAreNotListMarkers()
    {
        var hyphen=new[]{R(10,300,10,26,"They finally signed the long awaited inter-"),R(10,180,30,46,"national agreement")};
        Assert.Equal(new[]{"They finally signed the long awaited inter-national agreement"},Reflow(hyphen));
        var decimalStart=new[]{R(10,300,10,26,"the project is expected to cost roughly"),R(10,300,30,46,"3.5 million dollars over the next two"),R(10,90,50,66,"years")};
        Assert.Single(Reflow(decimalStart));
    }

    [Fact]
    public void TwoCapitalisedLinesAreNotAssumedToBeOneSentence()
    {
        var headlines=new[]{R(10,300,10,26,"Breaking news from the capital city"),R(10,290,30,46,"Markets fall sharply for a third day")};
        Assert.Equal(2,Reflow(headlines).Length);
    }

    [Fact]
    public void ChineseFirstLineIndentStaysInTheSameParagraphAndAnIndentedLineStartsANewOne()
    {
        var indented=new[]
        {
            R(40,300,10,30,"这一段的第一行按照中文习惯缩进了两格"),
            R(12,300,32,52,"后面的行顶格书写并且一直写到这一行的末尾"),
            R(12,150,54,74,"最后一行比较短。"),
            R(40,300,76,96,"新的一段同样从缩进两格的位置开始书写内"),
            R(12,120,98,118,"容并在此结束。")
        };
        Assert.Equal(new[]
        {
            "这一段的第一行按照中文习惯缩进了两格后面的行顶格书写并且一直写到这一行的末尾最后一行比较短。",
            "新的一段同样从缩进两格的位置开始书写内容并在此结束。"
        },Reflow(indented));
    }

    [Fact]
    public void CenteredChineseLinesMergeOnlyAfterAComma()
    {
        var subtitle=new[]{R(100,400,10,34,"我从来没有想过事情会这样结束，"),R(170,330,38,62,"但我们还是走到了这里")};
        Assert.Single(Reflow(subtitle));
        var poster=new[]{R(150,350,10,34,"年度工作总结大会"),R(100,400,38,62,"二〇二六年度优秀员工表彰仪式")};
        Assert.Equal(2,Reflow(poster).Length);
    }

    [Fact]
    public void MixedChineseAndLatinJoinsWithoutInsertingASpaceAtTheBoundary()
    {
        var mixed=new[]{R(10,300,10,30,"这个脚本需要先安装好依赖然后再使用"),R(10,200,32,52,"Python 来运行它")};
        Assert.Equal(new[]{"这个脚本需要先安装好依赖然后再使用Python 来运行它"},Reflow(mixed));
    }

    [Fact]
    public void SplitFragmentsOfOneRowAreJoinedButDistantColumnsAreNot()
    {
        var fragments=new[]{R(10,150,10,26,"the quick brown fox jumps"),R(158,300,11,27,"over the lazy dog and"),R(10,120,30,46,"keeps running")};
        Assert.Equal(new[]{"the quick brown fox jumps over the lazy dog and keeps running"},Reflow(fragments));
        var columns=new[]{R(10,200,10,26,"left column first line of"),R(320,520,10,26,"right column first line of"),R(10,120,30,46,"text here"),R(320,430,30,46,"other text")};
        Assert.Equal(4,Reflow(columns).Length);
    }

    [Fact]
    public void MissingGeometryOrASingleRegionReturnsTheOcrTextUntouched()
    {
        var noGeometry=new[]{new OcrRegion("He tried to get him to leave",.9f,Array.Empty<OcrPoint>()),R(12,67,30,44,"him alone")};
        Assert.Equal("raw",OcrTextLayout.Reflow(noGeometry,"raw"));
        Assert.Equal("raw",OcrTextLayout.Reflow(new[]{R(1,90,1,20,"only one line")},"raw"));
        Assert.Equal("raw",OcrTextLayout.Reflow(null,"raw"));
        Assert.Equal("raw",OcrTextLayout.Reflow(Array.Empty<OcrRegion>(),"raw"));
    }
}
