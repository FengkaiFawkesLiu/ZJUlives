using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Shike;
// A camera over world coordinates. Viewport edges never constrain node positions.
public sealed class GraphViewport : Grid
{
    readonly TranslateTransform pan=new();
    public ScaleTransform Zoom { get; }=new(1,1);
    public double HorizontalOffset=>-pan.X;
    public double VerticalOffset=>-pan.Y;
    public double ViewportWidth=>ActualWidth;
    public double ViewportHeight=>ActualHeight;
    public GraphViewport(Canvas canvas){Background=Brushes.Transparent;ClipToBounds=true;FocusVisualStyle=null;var transform=new TransformGroup();transform.Children.Add(Zoom);transform.Children.Add(pan);canvas.RenderTransform=transform;canvas.HorizontalAlignment=HorizontalAlignment.Left;canvas.VerticalAlignment=VerticalAlignment.Top;Children.Add(canvas);}
    public void ScrollToHorizontalOffset(double value)=>pan.X=-value;
    public void ScrollToVerticalOffset(double value)=>pan.Y=-value;
    public void ZoomAt(double value,Point anchor){double old=Zoom.ScaleX;double x=(anchor.X-pan.X)/old,y=(anchor.Y-pan.Y)/old;Zoom.ScaleX=Zoom.ScaleY=value;pan.X=anchor.X-x*value;pan.Y=anchor.Y-y*value;}
    public double Fit(IEnumerable<Point> points){var list=points.ToList();if(list.Count==0)return 1;double left=list.Min(p=>p.X),right=list.Max(p=>p.X),top=list.Min(p=>p.Y),bottom=list.Max(p=>p.Y);double zoom=Math.Clamp(Math.Min(Math.Max(1,ActualWidth-180)/Math.Max(1,right-left),Math.Max(1,ActualHeight-140)/Math.Max(1,bottom-top)),.01,1.8);Zoom.ScaleX=Zoom.ScaleY=zoom;pan.X=ActualWidth/2-(left+right)/2*zoom;pan.Y=ActualHeight/2-(top+bottom)/2*zoom;return zoom;}
}
