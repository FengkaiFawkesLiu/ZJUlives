using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Shike;

// Fixed simulation steps keep drag response independent of display refresh rate.
internal sealed class GraphPhysics
{
    readonly Dictionary<string, Point> positions;
    readonly Dictionary<string, Vector> velocities;
    readonly List<NoteEdge> edges;
    readonly string[] keys;
    public string? Held { get; set; }
    public GraphPhysics(Dictionary<string, Point> positions, List<NoteEdge> edges)
    {
        this.positions = positions; this.edges = edges; keys = positions.Keys.ToArray();
        velocities = keys.ToDictionary(k => k, _ => new Vector());
    }
    public void Move(string key, Point point)
    {
        var delta = point - positions[key];
        velocities[key] = new Vector(Math.Clamp(delta.X, -8, 8), Math.Clamp(delta.Y, -8, 8));
        positions[key] = point;
    }
    public void Stop() { foreach (var key in keys) velocities[key] = new Vector(); }
    public double Step(double width, double height)
    {
        var center=keys.Length==0?new Point():Held!=null?positions[Held]:new Point(positions.Values.Average(p=>p.X),positions.Values.Average(p=>p.Y));
        var force = keys.ToDictionary(k => k, k => new Vector((center.X - positions[k].X) * .0018, (center.Y - positions[k].Y) * .0018));
        for (int i = 0; i < keys.Length; i++) for (int j = i + 1; j < keys.Length; j++)
        {
            var delta = positions[keys[j]] - positions[keys[i]];
            if (delta.Length < .01) delta = new Vector(1, 1);
            double distance = delta.Length;
            var push = delta / distance * Math.Min(4, 6000 / Math.Max(400, distance * distance));
            force[keys[i]] -= push; force[keys[j]] += push;
        }
        foreach (var edge in edges)
        {
            var delta = positions[edge.To] - positions[edge.From]; double distance = Math.Max(1, delta.Length);
            var pull = delta / distance * ((distance - 190) * .018);
            force[edge.From] += pull; force[edge.To] -= pull;
        }
        double speed = 0;
        foreach (var key in keys)
        {
            if (key == Held) continue;
            var v = (velocities[key] + force[key]) * .88;
            v = new Vector(Math.Clamp(v.X, -12, 12), Math.Clamp(v.Y, -12, 12));
            var next = positions[key] + v;
            positions[key] = next; velocities[key] = v; speed += v.Length;
        }
        return speed;
    }
}
