using System.Drawing;

namespace Colo.Core;

/// <summary>
/// Un elemento de la pantalla leído por UI Automation. Raw: el rectángulo entero que da la app (sin recortar a la
/// pantalla), para reconocer el mismo elemento leído por otro camino. In: el nodo del árbol que lo contiene.
/// </summary>
public sealed record ScreenElement(string Role, string Name, string Value, Rectangle Rect, string State, Rectangle Raw, string In = "");

/// <summary>Cómo se arma la lista de elementos que ve el modelo.</summary>
public static class ScreenList
{
    /// <summary>
    /// Qué entra primero si hay que cortar: lo que el programa abrió encima (0), lo que tiene el foco (1), los
    /// campos (2), el resto de la ventana (3) y la barra de tareas (4). El orden del árbol no sirve: en WhatsApp la
    /// caja del mensaje va al final, tras las conversaciones y los mensajes, y se quedaba fuera.
    /// </summary>
    public static int Rank(ScreenElement e, bool popup = false, bool tray = false) =>
        popup ? 0 : tray ? 4 : e.State.StartsWith("foco", StringComparison.Ordinal) ? 1 : e.Role == "campo" ? 2 : 3;

    /// <summary>Sin repetidos (mismo rectángulo), hasta max por prioridad, y en orden de lectura (filas, izquierda a derecha).</summary>
    public static List<ScreenElement> Cap(IEnumerable<(ScreenElement E, int Rank)> items, int max, out int cut)
    {
        var unique = items.OrderBy(x => x.Rank).Select(x => x.E).GroupBy(e => e.Rect).Select(g => g.First()).ToList();
        cut = Math.Max(0, unique.Count - max);
        return Reading(unique.Take(max));
    }

    /// <summary>
    /// Junta a la lista lo leído aparte (bajo el ratón, el foco): si ya estaba, le suma el estado; si no (cortado,
    /// de un tipo que la lista no lee o la lectura no contestó), entra como uno más.
    /// </summary>
    public static List<ScreenElement> Merge(IReadOnlyList<ScreenElement> list, IReadOnlyList<ScreenElement> extra)
    {
        var result = new List<ScreenElement>(list);
        if (extra.Count == 0) return result;
        foreach (var x in extra)
        {
            var i = result.FindIndex(e => e.Raw == x.Raw);
            if (i < 0) result.Add(x);
            else result[i] = result[i] with { State = AddState(result[i].State, x.State) };
        }
        return Reading(result);
    }

    /// <summary>«foco» + «foco, ratón» = «foco, ratón»: cada estado una vez, en el orden en que llegó.</summary>
    public static string AddState(string have, string add)
    {
        var states = have.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var s in add.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            if (!states.Contains(s)) states.Add(s);
        return string.Join(", ", states);
    }

    static List<ScreenElement> Reading(IEnumerable<ScreenElement> items) =>
        items.OrderBy(e => e.Rect.Y / 8).ThenBy(e => e.Rect.X).ToList();
}
