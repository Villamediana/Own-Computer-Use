using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Colo.Core;

namespace Colo;

/// <summary>
/// El cofre en Windows: cifrado con DPAPI (solo este usuario en este PC lo abre), escribir el secreto en el campo
/// con foco sin que pase por el modelo, y copiarlo al portapapeles fuera del historial y por poco tiempo.
/// </summary>
static class Secrets
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Colo.Vault.v1");

    public static Vault Open(string path)
    {
        var vault = new Vault(path,
            data => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser),
            data => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        // Si lo dijo en voz alta, la transcripción quedó en el registro: se tapa.
        vault.Saved += Log.Redact;
        return vault;
    }

    public static async Task<string> RunAsync(Vault vault, ToolCall c, AiCursor? cursor, CancellationToken ct)
    {
        var name = c.Str("name");
        switch (c.Str("action"))
        {
            case "save":
            {
                var secret = c.Str("secret");
                var from = c.Str("from");
                if (secret.Length == 0 && from.Length > 0)
                {
                    secret = from switch
                    {
                        "clipboard" => Desktop.Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "") ?? "",
                        "selection" => await Desktop.SelectionRawAsync(ct) ?? "",
                        "focused" => await Task.Run(Desktop.FocusedRaw, ct),
                        _ => "",
                    };
                    secret = secret.Trim();
                    if (secret.Length == 0)
                        return from == "focused"
                            ? "error: no pude leer el campo (los campos de contraseña no se dejan leer): que lo copie y usa from clipboard, o que lo diga"
                            : $"error: no hay texto en {from}";
                }
                cursor?.Say("Guardando en el cofre");
                return vault.Save(name, c.Str("kind"), c.Str("user"), c.Str("host"), c.Str("where"), secret, c.Bool("replace"));
            }
            case "forget":
                return vault.Forget(name);
            case "copy":
            {
                var secret = vault.Reveal(name, out var entry);
                if (entry is null) return $"error: no hay «{name}» en el cofre";
                if (secret is null) return "error: no pude abrir ese secreto (¿cofre de otro usuario o de otro PC?)";
                CopyHidden(secret);
                cursor?.Say("Copiado (30 s)");
                return $"ok: la clave de «{entry.Name}» está en el portapapeles 30 s (fuera del historial)";
            }
            default: // type
            {
                var field = c.Str("field");
                var secret = vault.Reveal(name, out var entry);
                if (entry is null) return $"error: no hay «{name}» en el cofre (mira «Cofre»)";
                var text = field switch { "user" => entry.User, "host" => entry.Host, _ => secret };
                if (text is null) return "error: no pude abrir ese secreto (¿cofre de otro usuario o de otro PC?)";
                if (text.Length == 0) return $"error: «{entry.Name}» no tiene {field}";
                if (field is not ("user" or "host") && await Task.Run(UnsafeFocus, ct) is { } why) return "error: " + why;
                cursor?.Say(field is "user" or "host" ? "Escribiendo…" : "Escribiendo ••••");
                Input.Type(text);
                if (c.Bool("submit")) { await Task.Delay(40, ct); Input.Combo(Colo.Core.Keys.Parse("enter")); }
                return field is "user" or "host" ? "ok" : "ok: escrito (oculto)";
            }
        }
    }

    /// <summary>
    /// Por qué no se escribe un secreto donde está el foco: en la barra de direcciones o el buscador se iría a
    /// internet. null = se puede.
    /// </summary>
    static string? UnsafeFocus()
    {
        try
        {
            var e = AutomationElement.FocusedElement;
            if (e is null) return null;
            if (e.Current.IsPassword) return null;
            var name = WindowsComputer.Fold(e.Current.Name ?? "");
            if (name.Contains("address") || name.Contains("endereco") || name.Contains("direccion") || name.Contains("url")
                || name.Contains("search") || name.Contains("pesquis") || name.Contains("busca"))
                return "el foco está en la barra de direcciones o un buscador; haz clic en el campo de la contraseña antes";
        }
        catch (ElementNotAvailableException) { }
        return null;
    }

    /// <summary>Al portapapeles marcado para que Windows (Win+V, nube) y otros programas no lo guarden; se borra a los 30 s.</summary>
    static void CopyHidden(string secret)
    {
        Desktop.Sta(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, secret);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream([0, 0, 0, 0]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
            Clipboard.SetDataObject(data, true);
            return 0;
        });
        _ = Task.Delay(30_000).ContinueWith(_ =>
        {
            try
            {
                Desktop.Sta(() =>
                {
                    if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear();
                    return 0;
                });
            }
            catch { }
        });
    }
}
