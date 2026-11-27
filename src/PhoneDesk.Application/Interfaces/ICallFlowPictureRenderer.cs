using PhoneDesk.Operations;

namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Draws the call-flow diagram of an auto attendant as a PNG picture, from the same layout and
/// colors the SVG text is written from. Both heads use the same implementation, so the report of
/// the desktop app and of the MCP server hold the same pictures.
/// </summary>
public interface ICallFlowPictureRenderer
{
    byte[] RenderPng(CallFlowDiagram diagram);
}
