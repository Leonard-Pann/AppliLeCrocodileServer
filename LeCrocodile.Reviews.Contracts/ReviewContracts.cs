namespace LeCrocodile.Reviews.Contracts;

public sealed record SubmitReviewRequest(string UserName, int Rating, string Comment);

public sealed record ReviewResponse(
    long Id,
    string UserName,
    int Rating,
    string Comment,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsOwnReview);

public sealed record ReviewSummary(string CocktailId, long TotalCount, double AverageRating);

public sealed record ReviewPage(
    ReviewResponse[] Items,
    long TotalCount,
    double AverageRating,
    long? NextBefore);
