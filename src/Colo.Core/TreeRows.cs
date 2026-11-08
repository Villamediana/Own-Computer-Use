using System.Drawing;

namespace Colo.Core;

/// <summary>
/// En las apps web (Outlook nuevo) un nodo desplegado abarca a sus hijos: su centro cae en una carpeta del medio.
/// Su propia fila va desde arriba hasta donde empieza el primer nodo de dentro.
/// </summary>
public static class TreeRows
{
    /// <summary>La fila propia de node, o null si no tiene nodos dentro (ya es solo su fila).</summary>
    public static Rectangle? Own(Rectangle node, IEnumerable<Rectangle> nodes)
    {
        var top = nodes.Where(o => o != node && o.Top > node.Top + 2 && o.Bottom <= node.Bottom + 2
                && o.Left >= node.Left - 2 && o.Right <= node.Right + 2)
            .Select(o => (int?)o.Top).Min();
        return top is int t ? Rectangle.FromLTRB(node.Left, node.Top, node.Right, t) : null;
    }
}
