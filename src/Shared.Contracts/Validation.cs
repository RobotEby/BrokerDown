namespace Shared.Contracts;

public static class Money
{
    public static bool IsValid(decimal amount) =>
        amount > 0 && amount <= 9999999999999999.99m && decimal.Round(amount, 2) == amount;
}
