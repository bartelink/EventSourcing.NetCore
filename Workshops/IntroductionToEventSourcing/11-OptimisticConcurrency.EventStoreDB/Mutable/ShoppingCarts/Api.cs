using Core.Validation;
using EventStore.Client;
using Microsoft.AspNetCore.Mvc;
using OptimisticConcurrency.Core.EventStoreDB;
using OptimisticConcurrency.Mutable.Pricing;
using static Microsoft.AspNetCore.Http.TypedResults;
using static System.DateTimeOffset;

namespace OptimisticConcurrency.Mutable.ShoppingCarts;

public static class Api
{
    public static WebApplication ConfigureMutableShoppingCarts(this WebApplication app)
    {
        var clients = app.MapGroup("/api/mutable/clients/{clientId:guid}/");
        var shoppingCarts = clients.MapGroup("shopping-carts");
        var shoppingCart = shoppingCarts.MapGroup("{shoppingCartId:guid}");
        var productItems = shoppingCart.MapGroup("product-items");

        shoppingCarts.MapPost("",
            async (EventStoreClient eventStore,
                Guid clientId,
                CancellationToken ct) =>
            {
                var shoppingCartId = Uuid.NewUuid().ToGuid();

                await eventStore.Add(shoppingCartId,
                    ShoppingCart.Open(shoppingCartId, clientId.NotEmpty(), Now), ct);

                return Created($"/api/mutable/clients/{clientId}/shopping-carts/{shoppingCartId}", shoppingCartId);
            }
        );

        productItems.MapPost("",
            async (
                IProductPriceCalculator pricingCalculator,
                EventStoreClient eventStore,
                Guid shoppingCartId,
                AddProductRequest body,
                CancellationToken ct) =>
            {
                var productItem = body.ProductItem.NotNull().ToProductItem();

                await eventStore.GetAndUpdate(shoppingCartId,
                    state => state.AddProduct(pricingCalculator, productItem, Now), ct);

                return NoContent();
            }
        );

        productItems.MapDelete("{productId:guid}",
            async (
                EventStoreClient eventStore,
                Guid shoppingCartId,
                [FromRoute] Guid productId,
                [FromQuery] int? quantity,
                [FromQuery] decimal? unitPrice,
                CancellationToken ct) =>
            {
                var productItem = new PricedProductItem
                {
                    ProductId = productId.NotEmpty(),
                    Quantity = quantity.NotNull().Positive(),
                    UnitPrice = unitPrice.NotNull().Positive()
                };

                await eventStore.GetAndUpdate(shoppingCartId,
                    state => state.RemoveProduct(productItem, Now),
                    ct);

                return NoContent();
            }
        );

        shoppingCart.MapPost("confirm",
            async (EventStoreClient eventStore,
                Guid shoppingCartId,
                CancellationToken ct) =>
            {
                await eventStore.GetAndUpdate(shoppingCartId,
                    state => state.Confirm(Now), ct);

                return NoContent();
            }
        );

        shoppingCart.MapDelete("",
            async (EventStoreClient eventStore,
                Guid shoppingCartId,
                CancellationToken ct) =>
            {
                await eventStore.GetAndUpdate(shoppingCartId,
                    state => state.Cancel(Now), ct);

                return NoContent();
            }
        );

        shoppingCart.MapGet("",
            async Task<IResult> (
                EventStoreClient eventStore,
                Guid shoppingCartId,
                CancellationToken ct) =>
            {
                var result = await eventStore.GetShoppingCart(shoppingCartId, ct);

                return result is not null ? Ok(result) : NotFound();
            }
        );

        return app;
    }
}

public record ProductItemRequest(
    Guid? ProductId,
    int? Quantity
)
{
    public ProductItem ToProductItem() => new() { ProductId = ProductId.NotEmpty(), Quantity = Quantity.NotNull().Positive()};
}

public record AddProductRequest(
    ProductItemRequest? ProductItem
);
