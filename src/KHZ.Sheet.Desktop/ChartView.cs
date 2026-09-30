using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using KHZ.Sheet.Core;

namespace KHZ.Sheet.Desktop;

public sealed class ChartView : FrameworkElement
{
    private readonly ChartDefinition _chart;
    private IReadOnlyList<ChartPoint> _points;
    public ChartView(ChartDefinition chart,IReadOnlyList<ChartPoint> points) { _chart=chart;_points=points;MinWidth=300;MinHeight=200;Update(points); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    public void Update(IReadOnlyList<ChartPoint> points)
    {
        _points=points;
        AutomationProperties.SetName(this,_chart.Title);
        AutomationProperties.SetHelpText(this,string.Join("; ",points.Select(p=>p.Category+": "+(p.Value is KhzRational q?$"{q.Numerator}/{q.Denominator}":"missing/error"))));
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width=Math.Max(300,ActualWidth),height=Math.Max(200,ActualHeight),left=70,top=42,right=width-25,bottom=height-58;
        dc.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
        void Text(string text,double x,double y,double size=12)=>dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        Text(_chart.Title,18,12,16);
        double[] values=_points.Where(p=>p.Value.HasValue).Select(p=>(double)p.Value!.Value.Numerator/p.Value.Value.Denominator).ToArray();
        if(values.Length==0) {Text("No numeric values",left,top);return;}
        double min=Math.Min(0,values.Min()),max=Math.Max(0,values.Max());if(min==max)max=min+1;
        double Y(double v)=>bottom-(v-min)/(max-min)*(bottom-top);
        double X(double v)=>left+(v-min)/(max-min)*(right-left);
        Pen axis=new(Brushes.Gray,1),line=new(Brushes.RoyalBlue,2);
        dc.DrawLine(axis,new Point(left,bottom),new Point(right,bottom));dc.DrawLine(axis,new Point(left,top),new Point(left,bottom));
        if (_chart.Kind == ChartKind.Bar)
        {
            Text(min.ToString("G4",CultureInfo.InvariantCulture),left,bottom+12);
            Text(max.ToString("G4",CultureInfo.InvariantCulture),right-40,bottom+12);
            dc.DrawLine(axis,new Point(X(0),top),new Point(X(0),bottom));
        }
        else
        {
            Text(max.ToString("G4",CultureInfo.InvariantCulture),5,top);Text(min.ToString("G4",CultureInfo.InvariantCulture),5,bottom-12);
            dc.DrawLine(axis,new Point(left,Y(0)),new Point(right,Y(0)));
        }
        Point? prior=null;int count=_points.Count;
        for(int i=0;i<count;++i) {
            ChartPoint p=_points[i];double step=(_chart.Kind==ChartKind.Bar?bottom-top:right-left)/count;
            if(p.Value is not KhzRational q) {prior=null;continue;}
            double v=(double)q.Numerator/q.Denominator;
            double x=left+(i+.5)*step;
            if(_chart.Kind==ChartKind.Bar) {
                double y=top+i*step;dc.DrawRectangle(Brushes.RoyalBlue,null,new Rect(Math.Min(X(0),X(v)),y,Math.Max(1,Math.Abs(X(v)-X(0))),Math.Max(.5,step*.65)));
                if(i%Math.Max(1,count/12)==0)Text(p.Category[..Math.Min(10,p.Category.Length)],5,y);
            } else if(_chart.Kind==ChartKind.Column) {
                dc.DrawRectangle(Brushes.RoyalBlue,null,new Rect(x-step*.32,Math.Min(Y(v),Y(0)),Math.Max(.5,step*.64),Math.Max(1,Math.Abs(Y(v)-Y(0)))));
            } else {
                Point point=new(x,Y(v));if(prior is Point previous)dc.DrawLine(line,previous,point);
                dc.DrawEllipse(Brushes.RoyalBlue,null,point,2,2);prior=point;
            }
            if(_chart.Kind!=ChartKind.Bar && i%Math.Max(1,count/10)==0)Text(p.Category[..Math.Min(10,p.Category.Length)],x-10,bottom+10);
        }
    }
}
