namespace LeCrocodile.Reviews.Contracts;

public sealed record SubmitReviewRequest(string UserName, int Rating, string Comment, int? AlcoholLevel = null);

public sealed record ReviewResponse(
    long Id,
    string UserName,
    int Rating,
    string Comment,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsOwnReview,
    int? AlcoholLevel = null);

public sealed record ReviewSummary(string CocktailId, long TotalCount, double AverageRating);

public sealed record ReviewPage(
    ReviewResponse[] Items,
    long TotalCount,
    double AverageRating,
    long? NextBefore,
    ReviewResponse? OwnReview = null,
    int? AlcoholLevel = null);

public sealed record AlcoholSummary(string CocktailId, int Level);

public sealed record AppCompatibility(string MinimumVersion);
