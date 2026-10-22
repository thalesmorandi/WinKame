using System.Diagnostics;
using WinKame.Core;

namespace WinKame.Shell;

/// <summary>Makes sure an external tool is present before an action starts, offering to install it.</summary>
internal static class ToolGate
{
    public static async Task<bool> EnsureAsync(ExternalTool tool)
    {
        if (ToolLocator.Find(tool) is not null) return true;
        var ok = Ui.Confirm(
            T("{0} não foi encontrado.", ToolLocator.DisplayName(tool)) + " " + ToolLocator.Purpose(tool) + "\n\n" +
            T("Instalar agora com o winget ({0})?", ToolLocator.WingetId(tool)),
            T("Ferramenta necessária"));
        if (!ok) return false;
        return await InstallAsync(tool);
    }

    /// <summary>Runs winget in a visible console so the user sees the download and any prompts.</summary>
    public static async Task<bool> InstallAsync(ExternalTool tool)
    {
        try
        {
            var args = $"install --id {ToolLocator.WingetId(tool)} -e --accept-source-agreements --accept-package-agreements";
            using var p = Process.Start(new ProcessStartInfo("winget.exe", args) { UseShellExecute = true });
            if (p is null) return false;
            await p.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            Ui.Error(T("Não foi possível rodar o winget: {0}", ex.Message) + "\n\n" + T("Instale {0} manualmente e indique o caminho em Configurações.", ToolLocator.DisplayName(tool)));
            return false;
        }
        ToolLocator.Invalidate();
        if (ToolLocator.Find(tool) is not null) return true;
        Ui.Error(T("{0} ainda não foi encontrado. Se a instalação terminou, indique o caminho do executável em Configurações › Ferramentas.", ToolLocator.DisplayName(tool)));
        return false;
    }
}
