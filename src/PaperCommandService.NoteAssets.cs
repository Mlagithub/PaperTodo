using System.IO;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    internal PaperNoteImage ReadNoteImage(string paperId, string imageId)
    {
        EnsureRunning();
        var paper = RequirePaper(RequiredId(paperId, "paperId"), PaperTypes.Note);
        if (!_controller.CaptureNoteSnapshot(paper).ContentAvailable)
            throw Error("note_content_unavailable", "Only built-in Markdown assets are available.");
        var id = RequiredId(imageId, "imageId");
        if (!_controller.ImageStore.TryReadOwnedImage(paper.Id, id,
                IPaperNoteAssetsApi.MaximumImageBytes, out var mime, out var bytes, out var tooLarge))
            throw Error(tooLarge ? "asset_too_large" : "asset_not_found",
                tooLarge ? "The encoded image exceeds the plugin read limit." : "The note image is unavailable.");
        return new PaperNoteImage(id, mime, bytes);
    }

    internal PaperNoteImage ImportNoteImage(string paperId, string mime, byte[] bytes)
    {
        EnsureRunning();
        var paper = RequirePaper(RequiredId(paperId, "paperId"), PaperTypes.Note);
        if (!_controller.CaptureNoteSnapshot(paper).ContentAvailable)
            throw Error("note_content_unavailable", "Only built-in Markdown assets are available.");
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw Error("invalid_params", "Image bytes must not be empty.");
        if (bytes.Length > IPaperNoteAssetsApi.MaximumImageBytes)
            throw Error("asset_too_large", "The image exceeds the maximum supported size.");

        try
        {
            var asset = _controller.ImageStore.ImportEncodedImage(paper.Id, bytes, mime);
            return new PaperNoteImage(asset.Id, asset.Mime, bytes);
        }
        catch (InvalidDataException ex)
        {
            throw Error("invalid_image_data", ex.Message);
        }
        catch (Exception ex)
        {
            throw Error("image_import_failed", ex.Message);
        }
    }
}
