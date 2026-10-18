namespace Conduit.Api;

// Response bodies, as RealWorld names them.

public record UserResponse(UserView User);

public record ProfileResponse(ProfileView Profile);

public record ArticleResponse(ArticleView Article);

public record ArticlesResponse(List<ArticleView> Articles, int ArticlesCount);

public record CommentResponse(CommentView Comment);

public record CommentsResponse(List<CommentView> Comments);

public record TagsResponse(List<string> Tags);

public record ErrorResponse(ErrorList Errors);

public record ErrorList(string[] Body);
