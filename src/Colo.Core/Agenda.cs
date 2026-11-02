using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Colo.Core;

/// <summary>
/// Una cosa de la agenda: tarea, recordatorio o línea de una lista. Due = para cuándo; Remind = cuándo avisar;
/// Repeat = daily, weekdays, weekly o monthly (al cumplirse pasa a la siguiente). Who = quién la tiene que hacer
/// (vacío = el usuario). Source = de dónde salió (una reunión…).
/// </summary>
public sealed record AgendaItem(int Id, string Text, string Project = "", string List = "", DateTime? Due = null,
    DateTime? Remind = null, string Repeat = "", bool Done = false, string Who = "", string Source = "",
    DateTime Created = default, DateTime? Closed = null);

/// <summary>Un proyecto: cómo va (Status) y un resumen que el modelo mantiene. Aliases = otros nombres que le da.</summary>
public sealed record AgendaProject(string Name, string Status = "", string Summary = "", List<string>? Aliases = null,
    DateTime Updated = default);

/// <summary>
/// Agenda personal: tareas, listas, recordatorios y estado de proyectos. Quien decide qué es cada cosa (proyecto,
/// lista, recordatorio) es el modelo con la herramienta agenda; aquí solo se guarda, se busca y se avisa.
/// Módulo aparte: si se quita, el resto de Colo no cambia.
/// </summary>
public sealed class Agenda
{
    public const int MaxOpen = 500;
    public static readonly string[] Repeats = ["daily", "weekdays", "weekly", "monthly"];

    sealed class Data
    {
        public List<AgendaItem> Items { get; set; } = [];
        public List<AgendaProject> Projects { get; set; } = [];
        public int Next { get; set; } = 1;
    }

    readonly string _path;
    readonly object _lock = new();
    Data _d = new();

    /// <summary>Otras fuentes para search (las actas de reuniones): devuelven líneas que ya coinciden.</summary>
    public Func<string, IEnumerable<string>>? MoreSearch { get; set; }

    /// <summary>Cambió algo (Ajustes se refresca).</summary>
    public event Action? Changed;

    public Agenda(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new(); }
        catch { _d = new(); }
    }

    public IReadOnlyList<AgendaItem> Items { get { lock (_lock) return [.. _d.Items]; } }
    public IReadOnlyList<AgendaProject> Projects { get { lock (_lock) return [.. _d.Projects]; } }

    /// <summary>La línea del turno: los nombres de los proyectos, para que el modelo los reconozca al oírlos.</summary>
    public string? ProjectsLine()
    {
        lock (_lock)
            return _d.Projects.Count == 0 ? null
                : "Proyectos: " + string.Join(" · ", _d.Projects.Select(p => p.Name + (p.Aliases is { Count: > 0 } a ? $" ({string.Join(", ", a)})" : "")));
    }

    // ───────────────────────── herramienta

    public string Run(ToolCall c, DateTime now) => c.Str("action") switch
    {
        "add" => Add(c.Str("text"), c.Str("project"), c.Str("list"), Date(c.Str("due")), Date(c.Str("remind")),
            c.Str("repeat"), c.Str("who"), c.Str("source"), now),
        "edit" => Edit(c, now),
        "done" => Done(Ref(c), now),
        "remove" => Remove(Ref(c)),
        "project" => Project(c.Str("project"), c.Str("status"), c.Str("summary"), c.Str("aliases"), now),
        "search" => Search(c.Str("text"), now),
        _ => List(c.Str("what"), c.Str("project"), c.Str("list"), now),
    };

    /// <summary>item llega como número (#3) o como texto.</summary>
    static string Ref(ToolCall c) => c.Int("item")?.ToString() ?? c.Str("item");

    public string Add(string text, string project, string list, DateTime? due, DateTime? remind, string repeat,
        string who, string source, DateTime now)
    {
        text = text.Trim();
        if (text.Length == 0) return "error: falta text";
        if (repeat.Length > 0 && !Repeats.Contains(repeat)) return "error: repeat es daily, weekdays, weekly o monthly";
        lock (_lock)
        {
            if (_d.Items.Count(i => !i.Done) >= MaxOpen) return $"error: hay {MaxOpen} cosas abiertas; cierra o borra algunas";
            // Con el nombre ya guardado del proyecto: «TF» y «Tower Finder» son el mismo.
            project = Canonical(project);
            var same = _d.Items.FirstOrDefault(i => !i.Done && Extract.Fold(i.Text) == Extract.Fold(text)
                && Extract.Fold(i.Project) == Extract.Fold(project));
            if (same is not null) return $"ok: ya estaba (#{same.Id})";
            var item = new AgendaItem(_d.Next++, text, project, list.Trim(), due, remind, repeat, Who: who.Trim(),
                Source: source.Trim(), Created: now);
            _d.Items.Add(item);
            Save();
            return $"ok: #{item.Id} {Line(item, now)}";
        }
    }

    string Edit(ToolCall c, DateTime now)
    {
        lock (_lock)
        {
            var i = FindItem(Ref(c));
            if (i < 0) return $"error: no hay «{Ref(c)}» (usa el #número de list)";
            var it = _d.Items[i];
            if (c.Str("text").Length > 0) it = it with { Text = c.Str("text").Trim() };
            if (c.Args.ContainsKey("project")) it = it with { Project = Canonical(c.Str("project")) };
            if (c.Args.ContainsKey("list")) it = it with { List = c.Str("list").Trim() };
            if (c.Args.ContainsKey("due")) it = it with { Due = Date(c.Str("due")) };
            if (c.Args.ContainsKey("remind")) it = it with { Remind = Date(c.Str("remind")) };
            if (c.Args.ContainsKey("who")) it = it with { Who = c.Str("who").Trim() };
            if (c.Args.ContainsKey("repeat"))
            {
                if (c.Str("repeat").Length > 0 && !Repeats.Contains(c.Str("repeat"))) return "error: repeat es daily, weekdays, weekly o monthly";
                it = it with { Repeat = c.Str("repeat") };
            }
            _d.Items[i] = it;
            Save();
            return $"ok: #{it.Id} {Line(it, now)}";
        }
    }

    /// <summary>Cerrar. Si se repite, no se cierra: pasa a la siguiente vez.</summary>
    public string Done(string item, DateTime now)
    {
        lock (_lock)
        {
            var i = FindItem(item);
            if (i < 0) return $"error: no hay «{item}» (usa el #número de list)";
            var it = _d.Items[i];
            if (it.Repeat.Length > 0 && (it.Due ?? it.Remind) is not null)
            {
                it = it with
                {
                    Due = it.Due is { } d ? After(d, it.Repeat, now) : null,
                    // Si el aviso ya pasó a la vez siguiente (sonó antes de hacerla), no se salta otra.
                    Remind = it.Remind is { } r ? (it.Due is { } d0 && r > d0 ? r : After(r, it.Repeat, now)) : null,
                };
                _d.Items[i] = it;
                Save();
                return $"ok: hecho; la próxima: {Line(it, now)}";
            }
            _d.Items[i] = it with { Done = true, Closed = now, Remind = null };
            Save();
            return $"ok: #{it.Id} hecho";
        }
    }

    public string Remove(string item)
    {
        lock (_lock)
        {
            var i = FindItem(item);
            if (i < 0) return $"error: no hay «{item}»";
            var it = _d.Items[i];
            _d.Items.RemoveAt(i);
            Save();
            return $"ok: #{it.Id} borrado";
        }
    }

    /// <summary>Quita lo abierto que vino de esa fuente (rehacer el acta de una reunión sin duplicar). Lo hecho queda.</summary>
    public int RemoveOpenFrom(string sourcePrefix)
    {
        if (sourcePrefix.Length == 0) return 0;
        lock (_lock)
        {
            var n = _d.Items.RemoveAll(i => !i.Done && i.Source.StartsWith(sourcePrefix, StringComparison.Ordinal));
            if (n > 0) Save();
            return n;
        }
    }

    public string Project(string name, string status, string summary, string aliases, DateTime now)
    {
        name = name.Trim();
        if (name.Length == 0) return "error: falta project";
        lock (_lock)
        {
            var i = FindProject(name);
            var alias = aliases.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (i < 0)
            {
                _d.Projects.Add(new AgendaProject(name, status.Trim(), summary.Trim(), alias.Count > 0 ? alias : null, now));
                Save();
                return $"ok: proyecto «{name}» creado";
            }
            var p = _d.Projects[i];
            var names = new[] { p.Name }.Concat(p.Aliases ?? []).Concat(alias).Select(Extract.Fold).ToHashSet();
            p = p with
            {
                Status = status.Length > 0 ? status.Trim() : p.Status,
                Summary = summary.Length > 0 ? summary.Trim() : p.Summary,
                Aliases = alias.Count > 0 ? [.. (p.Aliases ?? []).Union(alias, StringComparer.OrdinalIgnoreCase)] : p.Aliases,
                Updated = now,
            };
            _d.Projects[i] = p;
            // Lo apuntado con un alias antes de saberse que lo era pasa al nombre del proyecto.
            for (var k = 0; k < _d.Items.Count; k++)
                if (_d.Items[k].Project.Length > 0 && _d.Items[k].Project != p.Name && names.Contains(Extract.Fold(_d.Items[k].Project.Trim())))
                    _d.Items[k] = _d.Items[k] with { Project = p.Name };
            Save();
            return $"ok: «{p.Name}» actualizado";
        }
    }

    /// <summary>
    /// what: open (por defecto), today (vence o avisa hoy, o atrasado), overdue, done (cerradas los últimos 7 días),
    /// projects. project/list filtran.
    /// </summary>
    public string List(string what, string project, string list, DateTime now)
    {
        lock (_lock)
        {
            if (what == "projects")
            {
                if (_d.Projects.Count == 0) return "ok: no hay proyectos";
                var sb = new StringBuilder("ok:\n");
                foreach (var p in _d.Projects.OrderByDescending(p => p.Updated))
                {
                    var open = _d.Items.Count(i => !i.Done && SameProject(i.Project, p.Name));
                    sb.Append("- ").Append(p.Name);
                    if (p.Status.Length > 0) sb.Append(" · ").Append(p.Status);
                    sb.Append($" · {open} abiertas · {Ago(p.Updated, now)}");
                    if (p.Summary.Length > 0) sb.Append("\n  ").Append(p.Summary);
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
            var end = now.Date.AddDays(1);
            IEnumerable<AgendaItem> q = _d.Items;
            q = what switch
            {
                "done" => q.Where(i => i.Done && i.Closed >= now.AddDays(-7)),
                "today" => q.Where(i => !i.Done && ((i.Due ?? i.Remind) is { } t && t < end)),
                "overdue" => q.Where(i => !i.Done && ((i.Due ?? i.Remind) is { } t && t < now)),
                _ => q.Where(i => !i.Done),
            };
            if (project.Trim().Length > 0) q = q.Where(i => SameProject(i.Project, project));
            if (list.Trim().Length > 0) q = q.Where(i => Extract.Fold(i.List) == Extract.Fold(list.Trim()));
            var rows = q.OrderBy(i => i.Due ?? i.Remind ?? DateTime.MaxValue).ThenBy(i => i.Id).Take(60).ToList();
            if (rows.Count == 0) return "ok: nada";
            return "ok:\n" + string.Join("\n", rows.Select(i => $"#{i.Id} {Line(i, now)}"));
        }
    }

    /// <summary>Por palabras (sin acentos): tareas, proyectos y lo que aporten otros módulos (actas).</summary>
    public string Search(string text, DateTime now)
    {
        var words = Words(text);
        if (words.Length == 0) return "error: falta text";
        bool Hit(params string[] s) { var f = Extract.Fold(string.Join(" ", s)); return words.All(f.Contains); }
        var sb = new StringBuilder();
        lock (_lock)
        {
            foreach (var p in _d.Projects.Where(p => Hit(p.Name, p.Status, p.Summary, string.Join(" ", p.Aliases ?? []))))
                sb.AppendLine($"Proyecto {p.Name}{(p.Status.Length > 0 ? " · " + p.Status : "")}{(p.Summary.Length > 0 ? ": " + p.Summary : "")}");
            foreach (var i in _d.Items.Where(i => Hit(i.Text, i.Project, i.List, i.Who)).OrderBy(i => i.Done).ThenByDescending(i => i.Id).Take(40))
                sb.AppendLine($"#{i.Id} {Line(i, now)}{(i.Done ? " (hecho)" : "")}");
        }
        foreach (var l in MoreSearch?.Invoke(text) ?? []) sb.AppendLine(l);
        return sb.Length == 0 ? "ok: nada" : "ok:\n" + sb.ToString().TrimEnd();
    }

    // ───────────────────────── avisos

    /// <summary>
    /// Los recordatorios que ya tocan. Se marcan como avisados: el que se repite pasa a la siguiente vez y el que
    /// no, se queda abierto sin hora de aviso.
    /// </summary>
    public IReadOnlyList<AgendaItem> Fire(DateTime now)
    {
        lock (_lock)
        {
            var due = _d.Items.Where(i => !i.Done && i.Remind is { } r && r <= now).ToList();
            if (due.Count == 0) return [];
            _fired = (now, due);
            foreach (var it in due)
            {
                var i = _d.Items.IndexOf(it);
                _d.Items[i] = it.Repeat.Length > 0 ? it with { Remind = After(it.Remind!.Value, it.Repeat, now) } : it with { Remind = null };
            }
            Save();
            return due;
        }
    }

    (DateTime At, List<AgendaItem> Items)? _fired;

    /// <summary>
    /// El último aviso, si fue hace menos de 15 min: va en el turno para que «hecho» o «avísame en 10 minutos»
    /// sepan de qué hablan.
    /// </summary>
    public string? RecentNotice(DateTime now)
    {
        lock (_lock)
        {
            if (_fired is not { } f || now - f.At > TimeSpan.FromMinutes(15)) return null;
            var mins = (int)(now - f.At).TotalMinutes;
            return $"Aviso que le diste {(mins < 1 ? "ahora mismo" : $"hace {mins} min")}: " + string.Join(" · ", f.Items.Select(i => $"#{i.Id} {i.Text}"));
        }
    }

    /// <summary>La siguiente vez después de now (si el PC estuvo apagado varios días, no avisa de todas las pasadas).</summary>
    public static DateTime After(DateTime t, string repeat, DateTime now)
    {
        do
        {
            t = repeat switch
            {
                "daily" => t.AddDays(1),
                "weekly" => t.AddDays(7),
                "monthly" => t.AddMonths(1),
                "weekdays" => t.AddDays(t.DayOfWeek switch { DayOfWeek.Friday => 3, DayOfWeek.Saturday => 2, _ => 1 }),
                _ => DateTime.MaxValue,
            };
        } while (t <= now);
        return t;
    }

    // ───────────────────────── ayudas

    public static DateTime? Date(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
    }

    public static string Line(AgendaItem i, DateTime now)
    {
        var sb = new StringBuilder();
        if (i.Project.Length > 0) sb.Append('[').Append(i.Project).Append("] ");
        if (i.List.Length > 0) sb.Append('(').Append(i.List).Append(") ");
        sb.Append(i.Text);
        if (i.Who.Length > 0) sb.Append(" · ").Append(i.Who);
        if (i.Due is { } d) sb.Append(" · para ").Append(When(d, now)).Append(d < now && !i.Done ? " (atrasada)" : "");
        if (i.Remind is { } r) sb.Append(" · aviso ").Append(When(r, now));
        if (i.Repeat.Length > 0) sb.Append(" · ").Append(i.Repeat);
        return sb.ToString();
    }

    static string When(DateTime t, DateTime now)
    {
        var day = t.Date == now.Date ? "hoy" : t.Date == now.Date.AddDays(1) ? "mañana" : t.ToString("ddd dd/MM", CultureInfo.GetCultureInfo("es"));
        return t.TimeOfDay == TimeSpan.Zero ? day : $"{day} {t:HH:mm}";
    }

    static string Ago(DateTime t, DateTime now) => (now - t).TotalDays switch
    {
        < 1 => "hoy",
        < 2 => "ayer",
        var d => $"hace {(int)d} días",
    };

    static string[] Words(string s) => Extract.Fold(s).Split([' ', ',', '.', '·'], StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length > 1).ToArray();

    bool SameProject(string a, string b)
    {
        if (Extract.Fold(a.Trim()) == Extract.Fold(b.Trim())) return true;
        var i = FindProject(b);
        return i >= 0 && Extract.Fold(_d.Projects[i].Name) == Extract.Fold(a.Trim());
    }

    /// <summary>El nombre ya guardado del proyecto (o su alias) para no partir uno en dos por cómo se escribió.</summary>
    string Canonical(string project)
    {
        project = project.Trim();
        var i = FindProject(project);
        return i >= 0 ? _d.Projects[i].Name : project;
    }

    int FindProject(string name)
    {
        var f = Extract.Fold(name.Trim());
        if (f.Length == 0) return -1;
        return _d.Projects.FindIndex(p => Extract.Fold(p.Name) == f || (p.Aliases ?? []).Any(a => Extract.Fold(a) == f));
    }

    /// <summary>Por #número o, si no, la única abierta que contiene ese texto.</summary>
    int FindItem(string item)
    {
        var s = item.Trim().TrimStart('#');
        if (int.TryParse(s, out var id)) return _d.Items.FindIndex(i => i.Id == id);
        var f = Extract.Fold(s);
        if (f.Length == 0) return -1;
        var hits = _d.Items.Select((it, idx) => (it, idx)).Where(x => !x.it.Done && Extract.Fold(x.it.Text).Contains(f)).ToList();
        return hits.Count == 1 ? hits[0].idx : -1;
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_d));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
        Changed?.Invoke();
    }
}
