using System.Threading;
using System.Windows.Automation;
using System.Windows.Controls;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Settings renders validation errors under the field instead of in a tooltip, which is what
/// the audit asked for and what a sighted user needed. The tooltip was doing a second job
/// nobody noticed: WPF surfaces a ToolTip string as UIA HelpText, so a screen reader read the
/// message. The adorner that replaced it is a raw visual with no automation peer, so deleting
/// the tooltip took the message off UIA entirely — worst on the hotkey fields, where a
/// conflict is the reason Done refuses to close. The message rides on the field instead.
/// </summary>
public class InlineValidationTests
{
    [Fact]
    public void AValidationMessageIsReadableByAScreenReader()
    {
        RunSta(() =>
        {
            var box = new TextBox();
            AutomationProperties.SetHelpText(box, "Press the desired key combination.");

            InlineValidation.Show(box, "Ctrl+Shift+4 is already assigned to another action.");

            Assert.Equal(
                "Ctrl+Shift+4 is already assigned to another action.",
                AutomationProperties.GetHelpText(box));
        });
    }

    [Fact]
    public void ClearingItGivesTheFieldItsOwnHelpTextBack()
    {
        RunSta(() =>
        {
            var box = new TextBox();
            AutomationProperties.SetHelpText(box, "Enter a value from 10 to 60.");

            InlineValidation.Show(box, "Enter a number between 10 and 60.");
            InlineValidation.Clear(box);

            Assert.Equal("Enter a value from 10 to 60.", AutomationProperties.GetHelpText(box));
        });
    }

    /// <summary>A second error replacing the first must not park the error as the "original".</summary>
    [Fact]
    public void ASecondErrorDoesNotBecomeTheFieldsOwnHelpText()
    {
        RunSta(() =>
        {
            var box = new TextBox();
            AutomationProperties.SetHelpText(box, "Folder where captures are saved.");

            InlineValidation.Show(box, "That folder does not exist.");
            InlineValidation.Show(box, "That folder cannot be written to.");
            InlineValidation.Clear(box);

            Assert.Equal("Folder where captures are saved.", AutomationProperties.GetHelpText(box));
        });
    }

    /// <summary>A field that never carried help text gets an empty string back, not the error.</summary>
    [Fact]
    public void ClearingAFieldWithNoHelpTextLeavesNoneBehind()
    {
        RunSta(() =>
        {
            var box = new TextBox();
            InlineValidation.Show(box, "Enter a number between 5 and 20.");
            InlineValidation.Clear(box);

            Assert.True(string.IsNullOrEmpty(AutomationProperties.GetHelpText(box)));
        });
    }

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}
