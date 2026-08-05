using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.PcbSupply;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    [RelayCommand(CanExecute = nameof(CanSaveRecipe))]
    private async Task SaveRecipeAsync()
    {
        try
        {
            CurrentRecipe.Name = RecipeName.Trim();
            RecipeName = CurrentRecipe.Name;
            await _store.SaveRecipeAsync(CurrentRecipe);
            RefreshRecipeFiles();
            StatusMessage = $"Recipe saved: {RecipeName}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            StatusMessage = $"Recipe save failed: {exception.Message}";
        }
    }

    private bool CanSaveRecipe() => !string.IsNullOrWhiteSpace(RecipeName);

    [RelayCommand]
    private async Task LoadRecipeAsync(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        try
        {
            CurrentRecipe.ReplaceWith(
                await _store.LoadRecipeAsync(fileName));
            RecipeName = CurrentRecipe.Name;
            BuildPoints();
            StatusMessage = $"Recipe loaded: {RecipeName}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
        {
            StatusMessage = $"Recipe load failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private void NewRecipe()
    {
        CurrentRecipe.ReplaceWith(new Recipe { Name = "New" });
        RecipeName = CurrentRecipe.Name;
        BuildPoints();
        StatusMessage = "New recipe created";
    }
}
