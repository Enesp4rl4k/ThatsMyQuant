using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface ISharedPortfolioStateManager
{
    void SetBalance(VenueKind venue, string asset, decimal amount);
    void AddBalanceDelta(VenueKind venue, string asset, decimal delta);
    decimal GetTotalBalance(VenueKind venue, string asset);
    decimal GetAvailableBalance(VenueKind venue, string asset);
    bool TryReserve(VenueKind venue, string asset, decimal amount);
    bool TryReserveForOrder(VenueKind venue, string asset, string orderId, decimal amount);
    void ReleaseReservation(VenueKind venue, string asset, decimal amount);
    void ReleaseReservationForOrder(VenueKind venue, string asset, string orderId, decimal amount);
    void ReleaseAllReservationsForOrder(VenueKind venue, string asset, string orderId);
    void UpdatePosition(VenueKind venue, string symbol, decimal quantity);
    decimal GetPosition(VenueKind venue, string symbol);
    VenuePortfolioSnapshot GetVenueSnapshot(VenueKind venue);
    GlobalPortfolioSnapshot GetGlobalSnapshot();
    void Clear();
}
