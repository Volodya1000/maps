using CableRoutePlanner.Application;
using ReactiveUI;
namespace CableRoutePlanner.ViewModels;
public sealed class AboutViewModel(IScreen screen, IGeodataRepository data) : ReactiveObject, IRoutableViewModel
{
    public string UrlPathSegment => "О данных";
    public IScreen HostScreen { get; } = screen;
    public string Attribution => data.Info.Attribution;
    public string Region => data.Info.RegionId;
    public string Details => "Реальный локальный пакет OSM: район Дрозды — Новинки, Минск.\n" +
        data.Info.Status + "\nМосты OSM требуют проверки и не являются разрешёнными переходами.\n" +
        "Copernicus DEM и ESA WorldCover в текущий пакет не входят. Полный пакет Беларуси не подготовлен.\n" +
        "Оценка предназначена для предварительного планирования и не заменяет обследование, согласования и рабочий проект.\n" +
        "Приложение не использует внешнюю сеть. © OpenStreetMap contributors, ODbL 1.0.";
}
