using ZapretTester.App;
using Xunit;

namespace ZapretTester.Tests;

public sealed class UserFacingMessageTests
{
    [Fact]
    public void LegacyHelperConflictGetsRussianExplanationAndPreparationAction()
    {
        const string original = "Unowned modifying WinDivert handle: PID 8712, layer 0. Isolation is not verified.";
        var message = UserFacingMessage.Create(original);
        Assert.True(message.OfferPrepareFlowseal);
        Assert.Contains("другой Zapret", message.Text);
        Assert.Contains("8712", message.Text);
        Assert.Contains("Подготовить Flowseal к проверке", message.Text);
        Assert.DoesNotContain("Unowned", message.Text);
        Assert.Equal(original, message.TechnicalDetails);
    }

    [Theory]
    [InlineData("Готово.")]
    [InlineData("Проверка отменена.")]
    [InlineData("WinDivertOpen(layer=4): указанная служба не установлена")]
    [InlineData("REFLECT observation stopped.")]
    [InlineData("Unowned modifying WinDivert handle: PID 0, layer 0. Isolation is not verified.")]
    public void OtherErrorsDoNotSuggestTakingOverFlowseal(string original)
    {
        var message = UserFacingMessage.Create(original);
        Assert.False(message.OfferPrepareFlowseal);
        Assert.Equal(original, message.Text);
    }
}
