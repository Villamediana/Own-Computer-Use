using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Colo.Core;

namespace Colo;

/// <summary>
/// Mira la agenda cada 30 s en su propio hilo y avisa de los recordatorios que tocan. No pasa por el agente ni
/// por el modelo: no cuesta nada ni frena lo demás. Si se quita este archivo, la agenda sigue (sin avisos).
/// </summary>
sealed class Reminders : IDisposable
{
    readonly Agenda _agenda;
    readonly Action<IReadOnlyList<AgendaItem>> _notify;
    readonly Timer _timer;
    int _busy;

    public Reminders(Agenda agenda, Action<IReadOnlyList<AgendaItem>> notify)
    {
        _agenda = agenda;
        _notify = notify;
        _timer = new Timer(_ => Tick(), null, 5_000, 30_000);
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var due = _agenda.Fire(DateTime.Now);
            if (due.Count > 0)
            {
                Log.Write("agenda: aviso " + string.Join(" · ", due.Select(i => $"#{i.Id} {i.Text}")));
                _notify(due);
            }
        }
        catch (Exception ex) { Log.Write("agenda: aviso falló " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
