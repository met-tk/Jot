using Microsoft.UI.Xaml.Media;

namespace Journal.Services;

internal static class AgingImageSurfaceLoader
{
    public static Task<LoadedImageSurface> LoadAsync(string path)
    {
        var completion = new TaskCompletionSource<LoadedImageSurface>(TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = LoadedImageSurface.StartLoadFromUri(new Uri(Path.GetFullPath(path)));
        surface.LoadCompleted += OnLoadCompleted;

        return completion.Task;

        void OnLoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
        {
            sender.LoadCompleted -= OnLoadCompleted;
            if (args.Status == LoadedImageSourceLoadStatus.Success)
            {
                completion.TrySetResult(sender);
            }
            else
            {
                sender.Dispose();
                completion.TrySetException(new InvalidDataException($"Texture decode failed: {path} ({args.Status})."));
            }
        }
    }
}
